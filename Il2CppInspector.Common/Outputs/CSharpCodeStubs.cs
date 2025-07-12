// Copyright (c) 2017-2021 Katy Coe - http://www.djkaty.com - https://github.com/djkaty
// All rights reserved

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Il2CppInspector.Properties;
using Il2CppInspector.Reflection;
using Assembly = Il2CppInspector.Reflection.Assembly;
using CustomAttributeData = Il2CppInspector.Reflection.CustomAttributeData;
using MethodInfo = Il2CppInspector.Reflection.MethodInfo;
using TypeInfo = Il2CppInspector.Reflection.TypeInfo;
using MethodBase = Il2CppInspector.Reflection.MethodBase;
using FieldInfo = Il2CppInspector.Reflection.FieldInfo;
using PropertyInfo = Il2CppInspector.Reflection.PropertyInfo;
using EventInfo = Il2CppInspector.Reflection.EventInfo;

namespace Il2CppInspector.Outputs
{
    public class CSharpCodeStubs
    {
        private readonly TypeModel model;
        private Exception lastException;

        // Namespace prefixes whose contents should be skipped
        public List<string> ExcludedNamespaces { get; set; }

        // Make adjustments to ensure that the generated code compiles
        public bool MustCompile { get; set; }

        // Suppress binary metadata in code comments
        public bool SuppressMetadata { get; set; }

        private const string CGAttribute = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";
        private const string FBAttribute = "System.Runtime.CompilerServices.FixedBufferAttribute";
        private const string ExtAttribute = "System.Runtime.CompilerServices.ExtensionAttribute";
        private const string AsyncAttribute = "System.Runtime.CompilerServices.AsyncStateMachineAttribute";
        private const string DMAttribute = "System.Reflection.DefaultMemberAttribute";

        // Assembly attributes we have already emitted
        private HashSet<CustomAttributeData> usedAssemblyAttributes = new HashSet<CustomAttributeData>();
        private readonly object usedAssemblyAttributesLock = new object();

        // Generate detailed attribute information similar to AssemblyShims
        private string GenerateCustomClassAttributeInfo(TypeInfo type, string prefix = "")
        {
            if (SuppressMetadata)
                return "";

            var sb = new StringBuilder();
            sb.Append($"{prefix}[CustomClass(NestedLevel = \"{type.FullName.Count(c => c == '+')}\", ");
            sb.Append($"Name = \"{EscapeString(type.Name)}\", ");
            sb.Append($"AccessModifier = \"{EscapeString(type.GetAccessModifierStringRaw())}\", ");
            sb.Append($"Modifier = \"{EscapeString(string.Join(" ", type.GetModifierStringRaw()))}\", ");
            sb.Append($"Parent = \"{EscapeString(type.BaseType == null ? "" : GetFullNameWithGenerics(type.BaseType))}\", ");
            sb.Append($"Interfaces = \"{EscapeString(type.ImplementedInterfaces?.Any() == true ? string.Join("|", type.ImplementedInterfaces.Select(i => GetFullNameWithGenerics(i))) : "")}\", ");
            sb.Remove(sb.Length - 2, 2); // Remove trailing ", "
            sb.Append(")]\n");
            return sb.ToString();
        }

        private string GenerateCustomMethodAttributeInfo(MethodBase method, string methodType, string methodTypeName = "", string prefix = "")
        {
            if (SuppressMetadata)
                return "";

            var sb = new StringBuilder();
            sb.Append($"{prefix}[CustomMethod(NestedLevel = \"{method.DeclaringType.FullName.Count(c => c == '+')}\", ");
            sb.Append($"ClassName = \"{EscapeString(method.DeclaringType.Name)}\", ");
            sb.Append($"Type = \"{EscapeString(methodType)}\", ");
            sb.Append($"TypeName = \"{EscapeString(methodTypeName)}\", ");
            sb.Append($"AccessModifier = \"{EscapeString(method.GetAccessModifierStringRaw())}\", ");
            sb.Append($"Modifier = \"{EscapeString(string.Join(" ", method.GetModifierStringRaw()))}\", ");
            sb.Append($"Name = \"{EscapeString(method.Name)}\", ");
            if (method is MethodInfo mi)
                sb.Append($"ReturnType = \"{EscapeString(GetFullNameWithGenerics(mi.ReturnType))}\", ");
            else
                sb.Append($"ReturnType = \"\", ");
            sb.Append($"ParameterTypes = \"{EscapeString(string.Join("|", method.DeclaredParameters.Select(p => GetFullNameWithGenerics(p.ParameterType))))}\", ");
            sb.Append($"Slot = \"{(method.Definition.Slot != ushort.MaxValue ? method.Definition.Slot.ToString() : "0")}\"");
            sb.Append(")]\n");
            return sb.ToString();
        }

        private string GenerateCustomFieldAttributeInfo(FieldInfo field, string prefix = "")
        {
            if (SuppressMetadata)
                return "";

            var sb = new StringBuilder();
            sb.Append($"{prefix}[CustomField(NestedLevel = \"{field.DeclaringType.FullName.Count(c => c == '+')}\", ");
            sb.Append($"ClassName = \"{EscapeString(field.DeclaringType.Name)}\", ");
            sb.Append($"Offset = \"0x{field.Offset:X2}\", ");
            sb.Append($"AccessModifier = \"{EscapeString(field.GetAccessModifierStringRaw())}\", ");
            sb.Append($"Modifier = \"{EscapeString(string.Join(" ", field.GetModifierStringRaw()))}\", ");
            sb.Append($"Name = \"{EscapeString(field.Name)}\"");
            sb.Append($"Type = \"{EscapeString(GetFullNameWithGenerics(field.FieldType))}\", ");
            sb.Append(")]\n");
            return sb.ToString();
        }

        private string GenerateAttributeAttributeInfo(CustomAttributeData ca, string prefix = "")
        {
            if (SuppressMetadata)
                return "";

            var sb = new StringBuilder();
            sb.Append($"{prefix}[Attribute(Name = \"{EscapeString(ca.AttributeType.Name)}\"");
            if (ca.VirtualAddress.Start != 0)
            {
                sb.Append($", RVA = \"{(ca.VirtualAddress.Start - model.Package.BinaryImage.ImageBase).ToAddressString()}\"");
                sb.Append($", Offset = \"0x{model.Package.BinaryImage.MapVATR(ca.VirtualAddress.Start):X}\"");
            }
            sb.Append(")]\n");
            return sb.ToString();
        }

        private string GetFullNameWithGenerics(TypeInfo type)
        {
            if (type == null)
                return "";

            var fullName = (type.IsGenericParameter || type.Namespace == "") ? "" : type.Namespace + ".";
            fullName += type.IsGenericType ? type.BaseName : type.Name;
            if (type.IsArray && !fullName.EndsWith("[]"))
            {
                fullName += "[]";
            }
            if (type.IsGenericType && type.GenericTypeArguments.Length > 0)
            {
                fullName += "<";
                fullName += string.Join(", ", type.GenericTypeArguments.Select(p => GetFullNameWithGenerics(p)));
                fullName += ">";
            }
            return fullName;
        }

        private string EscapeString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;
            
            return input.Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        public CSharpCodeStubs(TypeModel model) => this.model = model;

        // Get the last error that occurred and clear the error state
        public Exception GetAndClearLastException() {
            var ex = lastException;
            lastException = null;
            return ex;
        }

        public void WriteSingleFile(string outFile) => WriteSingleFile(outFile, t => t.Index);

        public void WriteSingleFile<TKey>(string outFile, Func<TypeInfo, TKey> orderBy) {
            usedAssemblyAttributes.Clear();
            writeFile(outFile, model.Assemblies.SelectMany(x => x.DefinedTypes).OrderBy(orderBy));
        }

        public void WriteFilesByNamespace<TKey>(string outPath, Func<TypeInfo, TKey> orderBy, bool flattenHierarchy) {
            usedAssemblyAttributes.Clear();
            Parallel.ForEach(model.Assemblies.SelectMany(x => x.DefinedTypes).GroupBy(t => t.Namespace), ns => {
                var relPath = !string.IsNullOrEmpty(ns.Key) ? ns.Key : "global";
                writeFile(Path.Combine(outPath, (flattenHierarchy ? relPath : Path.Combine(relPath.Split('.'))) + ".cs"),
                    ns.OrderBy(orderBy));
            });
        }

        public void WriteFilesByAssembly<TKey>(string outPath, Func<TypeInfo, TKey> orderBy, bool separateAttributes) {
            usedAssemblyAttributes.Clear();
            Parallel.ForEach(model.Assemblies, asm => {
                // Sort namespaces into alphabetical order, then sort types within the namespaces by the specified sort function
                if (writeFile(Path.Combine(outPath, Path.GetFileNameWithoutExtension(asm.ShortName) + ".cs"), asm.DefinedTypes.OrderBy(t => t.Namespace).ThenBy(orderBy), outputAssemblyAttributes: !separateAttributes)
                    && separateAttributes) {
                    File.WriteAllText(Path.Combine(outPath, $"AssemblyInfo_{Path.GetFileNameWithoutExtension(asm.ShortName)}.cs"), generateAssemblyInfo(new [] {asm}));
                }
            });
        }

        // get real type name without generics
        private string GetRealTypeName(TypeInfo type) {
            var name = type.Name;
            // Remove backtick notation (e.g., `2)
            name = Regex.Replace(name, "`[0-9]+", "");
            // Remove generic parameter names in brackets (e.g., [K,V])
            name = Regex.Replace(name, @"\[[^\]]+\]", "");
            return name;
        }

        // Find the default namespace for an assembly using similar logic to DefaultNamespaceFinder
        private string FindDefaultNamespace(Reflection.Assembly assembly)
        {
            var namespaces = assembly.DefinedTypes
                .Select(t => t.Namespace)
                .Where(ns => !string.IsNullOrEmpty(ns) && ns != "XamlGeneratedNamespace")
                .Distinct()
                .ToArray();

            if (!namespaces.Any())
                return string.Empty;

            // Get assembly name without extension
            var assemblyName = Path.GetFileNameWithoutExtension(assembly.ShortName);

            // Group namespaces by first part
            var namespaceGroups = namespaces
                .GroupBy(ns => GetFirstNamespacePart(ns))
                .Select(g => new {
                    FirstPart = g.Key,
                    CommonPrefix = GetCommonNamespacePrefix(g.ToArray()),
                    Namespaces = g.ToArray()
                })
                .ToList();

            if (namespaceGroups.Count == 0)
                return string.Empty;

            if (namespaceGroups.Count == 1)
                return namespaceGroups[0].CommonPrefix;

            // Try to find a namespace group that matches or starts with the assembly name
            var bestMatch = namespaceGroups.FirstOrDefault(g => 
                assemblyName.Equals(g.CommonPrefix, StringComparison.OrdinalIgnoreCase) || 
                g.CommonPrefix.StartsWith(assemblyName + ".", StringComparison.OrdinalIgnoreCase));

            return bestMatch?.CommonPrefix ?? string.Empty;
        }

        private string GetFirstNamespacePart(string ns)
        {
            int dotIndex = ns.IndexOf('.');
            return dotIndex < 0 ? ns : ns.Substring(0, dotIndex);
        }

        private string GetCommonNamespacePrefix(string[] namespaces)
        {
            if (namespaces.Length == 0)
                return string.Empty;

            if (namespaces.Length == 1)
                return namespaces[0];

            string commonPrefix = namespaces[0];
            for (int i = 1; i < namespaces.Length; i++)
            {
                commonPrefix = GetCommonPrefix(commonPrefix, namespaces[i]);
            }

            return commonPrefix;
        }

        private string GetCommonPrefix(string a, string b)
        {
            var partsA = a.Split('.');
            var partsB = b.Split('.');
            var commonParts = new List<string>();

            int minLength = Math.Min(partsA.Length, partsB.Length);
            for (int i = 0; i < minLength; i++)
            {
                if (string.Equals(partsA[i], partsB[i], StringComparison.Ordinal))
                    commonParts.Add(partsA[i]);
                else
                    break;
            }

            return string.Join(".", commonParts);
        }

        private string ExtractLastPart(string first, string second)
        {
            if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second))
            {
                return second ?? string.Empty;
            }
            
            // Split both strings by dots
            string[] firstParts = first.Split('.');
            int firstLength = firstParts.Length;
            string[] secondParts = second.Split('.');
            int secondLength = secondParts.Length;
            string[] resultParts = new string[secondLength];
            for (int i = 0; i < secondLength; i++) {
                if (i >= firstLength || firstParts[i] != secondParts[i]) {
                    resultParts = secondParts.Skip(i).ToArray();
                    break;
                }
            }
            return string.Join(".", resultParts);
        }

        // get real path using default namespace finder
        private string GetRealPath(TypeInfo type) {
            string defaultNamespace = FindDefaultNamespace(type.Assembly);
            string namespaceAsPath;
            
            if (!string.IsNullOrEmpty(defaultNamespace) && type.Namespace.StartsWith(defaultNamespace))
            {
                // Remove the default namespace prefix to get the relative namespace
                if (type.Namespace.Length > defaultNamespace.Length && type.Namespace[defaultNamespace.Length] == '.')
                    namespaceAsPath = type.Namespace.Substring(defaultNamespace.Length + 1);
                else if (type.Namespace == defaultNamespace)
                    namespaceAsPath = string.Empty;
                else
                    namespaceAsPath = type.Namespace;
            }
            else
            {
                namespaceAsPath = type.Namespace;
            }
            
            string relPath = $"{namespaceAsPath}{(namespaceAsPath.Length > 0 ? "." : "")}{GetRealTypeName(type)}";
            return Path.Combine(relPath.Split('.'));
        }

        public void WriteFilesByClass(string outPath, bool flattenHierarchy) {
            usedAssemblyAttributes.Clear();
            Parallel.ForEach(model.Assemblies.SelectMany(x => x.DefinedTypes), type => {
                string relPath = GetRealPath(type);
                string outFile = Path.Combine(outPath, flattenHierarchy ? relPath : Path.Combine(relPath.Split('.')) + ".cs");
                writeFile(outFile, new[] {type});
            });
        }

        private string AppendNumberToDuplicatePath(HashSet<string> paths, string path) {
            if (!paths.Contains(path))
            {
                paths.Add(path);
                return path;
            }
            
            int i = 2;
            string numberedPath;
            do {
                numberedPath = path + $".{i}";
                i++;
            } while (paths.Contains(numberedPath));
            
            paths.Add(numberedPath);
            return numberedPath;
        }

        public HashSet<Assembly> WriteFilesByClassTree(string outPath, bool separateAttributes) {
            usedAssemblyAttributes.Clear();
            var usedAssemblies = new HashSet<Assembly>();
            var usedPaths = new HashSet<string>();
            var usedPathsLock = new object();

            // Each thread tracks its own list of used assemblies and they are merged as each thread completes
            Parallel.ForEach(model.Assemblies.SelectMany(x => x.DefinedTypes),
                () => new HashSet<Assembly>(),
                (type, _, used) => {
                    string relPath = GetRealPath(type);
                    string uniqueRelPath;
                    lock (usedPathsLock) {
                        uniqueRelPath = AppendNumberToDuplicatePath(usedPaths, relPath);
                    }
                    string outFile = Path.Combine(outPath, Path.GetFileNameWithoutExtension(type.Assembly.ShortName), $"{uniqueRelPath}.cs");
                    if (writeFile(outFile, new[] {type}, outputAssemblyAttributes: !separateAttributes))
                        used.Add(type.Assembly);
                    return used;
                },
                usedPartition => {
                    lock (usedAssemblies) usedAssemblies.UnionWith(usedPartition);
                }
            );

            if (separateAttributes && usedAssemblies.Any() && lastException == null)
                foreach (var asm in usedAssemblies)
                    File.WriteAllText(Path.Combine(outPath, Path.GetFileNameWithoutExtension(asm.ShortName), "AssemblyInfo.cs"), generateAssemblyInfo(new [] {asm}));

            return usedAssemblies;
        }

        // Create a Visual Studio solution
        public void WriteSolution(string outPath, string unityPath, string unityAssembliesPath) {
            // Required settings
            MustCompile = true;

            // Output source files in tree format with separate assembly attributes
            var assemblies = WriteFilesByClassTree(outPath, true);

            if (lastException != null)
                return;

            // Per-project (per-assembly) solution definition and configuration
            var slnProjectDefs = new StringBuilder();
            var slnProjectConfigs = new StringBuilder();

            foreach (var asm in assemblies) {
                var guid = Guid.NewGuid();
                var name = Path.GetFileNameWithoutExtension(asm.ShortName);
                var csProjFile = Path.Combine(name, $"{name}.csproj");

                var def = Resources.SlnProjectDefinition
                    .Replace("%PROJECTGUID%", guid.ToString())
                    .Replace("%PROJECTNAME%", name)
                    .Replace("%CSPROJRELATIVEPATH%", csProjFile);

                slnProjectDefs.Append(def);

                var config = Resources.SlnProjectConfiguration
                    .Replace("%PROJECTGUID%", guid.ToString());

                slnProjectConfigs.Append(config);

                // Determine all the assemblies on which this assembly depends
                var dependencyTypes = asm.DefinedTypes.SelectMany(t => t.GetAllTypeReferences())
                    .Union(asm.CustomAttributes.SelectMany(a => a.AttributeType.GetAllTypeReferences()))
                    .Distinct();
                var dependencyAssemblies = dependencyTypes.Select(t => t.Assembly).Distinct()
                    .Except(new[] {asm});
                
                // Only create project references to those assemblies actually output in our solution
                dependencyAssemblies = dependencyAssemblies.Intersect(assemblies);

                var referenceXml = string.Concat(dependencyAssemblies.Select(
                        a => $@"    <ProjectReference Include=""..\{a.ShortName.Replace(".dll", "")}\{a.ShortName.Replace(".dll", "")}.csproj""/>" + "\n"
                ));

                // Create a .csproj file using the project Guid
                var csProj = Resources.CsProjTemplate
                    .Replace("%PROJECTGUID%", guid.ToString())
                    .Replace("%ASSEMBLYNAME%", name)
                    .Replace("%UNITYPATH%", unityPath)
                    .Replace("%SCRIPTASSEMBLIES%", unityAssembliesPath)
                    .Replace("%PROJECTREFERENCES%", referenceXml);

                File.WriteAllText(Path.Combine(outPath, csProjFile), csProj);
            }

            // Merge everything into .sln file
            var sln = Resources.CsSlnTemplate
                .Replace("%PROJECTDEFINITIONS%", slnProjectDefs.ToString())
                .Replace("%PROJECTCONFIGURATIONS%", slnProjectConfigs.ToString());

            var filename = Path.GetFileName(outPath);
            if (filename == "")
                filename = "Il2CppProject";
            File.WriteAllText(Path.Combine(outPath, $"{filename}.sln"), sln);
        }

        private bool writeFile(string outFile, IEnumerable<TypeInfo> types, bool useNamespaceSyntax = true, bool outputAssemblyAttributes = true) {

            var nsRefs = new HashSet<string>();
            var code = new StringBuilder();
            var nsContext = "";
            var usedTypes = new List<TypeInfo>();

            // Determine all namespace references (note: this may include some that aren't actually used due to output suppression in generateType()
            // We have to do this first so that we can generate properly scoped type references in the code
            foreach (var type in types) {
                var refs = type.GetAllTypeReferences();
                var ns = refs.Where(r => !string.IsNullOrEmpty(r.Namespace) && r.Namespace != type.Namespace).Select(r => r.Namespace);
                nsRefs.UnionWith(ns);
            }

            // Determine assemblies used in this file
            var assemblies = types.Select(t => t.Assembly).Distinct();

            // Add assembly attribute namespaces to reference list
            if (outputAssemblyAttributes)
                nsRefs.UnionWith(assemblies.SelectMany(a => a.CustomAttributes).SelectMany(a => a.GetAllTypeReferences()).Select(x => x.Namespace));

            var results = new ConcurrentBag<Dictionary<TypeInfo, StringBuilder>>();

            // Generate each type
            Parallel.ForEach(types, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(Environment.ProcessorCount / 2, 1) },
                () => new Dictionary<TypeInfo, StringBuilder>(),
                (type, _, dict) => {
                    // Skip namespace and any children if requested
                    // if (ExcludedNamespaces?.Any(x => x == type.Namespace || type.Namespace.StartsWith(x + ".")) ?? false)
                    //     return dict;

                    // Don't output global::Locale if desired
                    if (MustCompile
                        && type.Name == "Locale" && type.Namespace == string.Empty
                        && type.BaseType.FullName == "System.Object"
                        && type.IsClass && type.IsSealed && type.IsNotPublic && !type.ContainsGenericParameters
                        && type.DeclaredMembers.Count == type.DeclaredMethods.Count
                        && type.GetMethods("GetText").Length == type.DeclaredMethods.Count)
                        return dict;

                    // Assembly.DefinedTypes returns nested types in the assembly by design - ignore them
                    if (type.IsNested)
                        return dict;

                    // Get code
                    var code = generateType(type, nsRefs);
                    if (code.Length > 0)
                        dict.Add(type, code);
                    return dict;
            },
            dict => results.Add(dict));

            // Flatten
            var sortedResults = results.SelectMany(d => d).ToDictionary(i => i.Key, i => i.Value);

            // Process in order according to original sorted type list
            foreach (var type in types) {
                if (!sortedResults.TryGetValue(type, out var text))
                    continue;

                // Determine if we need to change namespace (after we have established the code block is not empty)
                if (useNamespaceSyntax) {
                    if (type.Namespace != nsContext) {
                        if (!string.IsNullOrEmpty(nsContext))
                            code.Remove(code.Length - 1, 1).Append("}\n\n");

                        if (!string.IsNullOrEmpty(type.Namespace))
                            code.Append("namespace " + type.Namespace + "\n{\n");

                        nsContext = type.Namespace;
                    }

                    if (!string.IsNullOrEmpty(nsContext)) {
                        text.Insert(0, "\t");
                        text.Replace("\n", "\n\t");
                        text.Length--;
                    }
                }

                // Append namespace
                if (!useNamespaceSyntax)
                    code.Append($"// Namespace: {(!string.IsNullOrEmpty(type.Namespace) ? type.Namespace : "<global namespace>")}\n");
                
                // Append type definition
                code.Append(text);
                code.Append("\n");

                // Add to list of used types
                usedTypes.Add(type);
            }

            // Stop if nothing to output
            if (!usedTypes.Any())
                return false;

            // Close namespace
            if (useNamespaceSyntax && !string.IsNullOrEmpty(nsContext))
                code.Remove(code.Length - 1, 1).Append("}\n");
            
            // Determine using directives (put System namespaces first)
            nsRefs.Clear();
            foreach (var type in usedTypes) {
                var refs = type.GetAllTypeReferences();
                var ns = refs.Where(r => !string.IsNullOrEmpty(r.Namespace) && r.Namespace != type.Namespace).Select(r => r.Namespace);
                nsRefs.UnionWith(ns);
            }
            nsRefs.UnionWith(assemblies.SelectMany(a => a.CustomAttributes).SelectMany(a => a.GetAllTypeReferences()).Select(x => x.Namespace));

            var usings = nsRefs.OrderBy(n => (n.StartsWith("System.") || n == "System") ? "0" + n : "1" + n);

            // Ensure output directory exists and is not a file
            var dir = string.Join("_", Path.GetDirectoryName(outFile).Split(Path.GetInvalidPathChars()));
             if (!string.IsNullOrEmpty(dir)) {
                try {
                    Directory.CreateDirectory(dir);
                }
                catch (IOException ex) {
                    lastException = ex;
                    return false;
                }
            }

            // Sanitize leafname (might be class name with invalid characters)
            var leafname = string.Join("-", Path.GetFileName(outFile).Split(Path.GetInvalidFileNameChars()));

            outFile = Path.Combine(dir, leafname);

            // Create output file
            bool fileWritten = false;
            do {
                try {
                    using StreamWriter writer = new StreamWriter(new FileStream(outFile, FileMode.Create), Encoding.UTF8);

                    // Write preamble
                    writer.Write(@"/*
 * Generated code file by Il2CppInspector - http://www.djkaty.com - https://github.com/djkaty
 */

");

                    // Output using directives
                    writer.Write(string.Concat(usings.Select(n => $"using {n};\n")));
                    if (nsRefs.Any())
                        writer.Write("\n");

                    // Output assembly information and attributes
                    writer.Write(generateAssemblyInfo(assemblies, nsRefs, outputAssemblyAttributes) + "\n\n");

                    // Output type definitions
                    writer.Write(code);

                    fileWritten = true;
                }
                catch (IOException ex) {
                    // If we get "file is in use by another process", we are probably writing a duplicate class in another thread
                    // Wait a bit and try again
                    if ((uint) ex.HResult != 0x80070020)
                        throw;

                    System.Threading.Thread.Sleep(100);
                }
            } while (!fileWritten);

            return true;
        }

        private string generateAssemblyInfo(IEnumerable<Reflection.Assembly> assemblies, IEnumerable<string> namespaces = null, bool outputAssemblyAttributes = true) {
            var text = new StringBuilder();

            foreach (var asm in assemblies) {
                text.Append($"// Image {asm.Index}: {asm.ShortName} - Assembly: {asm.FullName}");
                if (!SuppressMetadata)
                    text.Append($" - Types {asm.ImageDefinition.TypeStart}-{asm.ImageDefinition.TypeStart + asm.ImageDefinition.TypeCount - 1}");
                text.AppendLine();

                // Assembly-level attributes
                if (outputAssemblyAttributes)
                    lock (usedAssemblyAttributesLock) {
                        text.Append(asm.CustomAttributes.Where(a => a.AttributeType.FullName != ExtAttribute)
                            .Except(usedAssemblyAttributes ?? new HashSet<CustomAttributeData>())
                            .OrderBy(a => a.AttributeType.Name)
                            .ToString(new Scope { Current = null, Namespaces = namespaces ?? new List<string>() }, attributePrefix: "assembly: ", emitPointer: !SuppressMetadata, mustCompile: MustCompile));
                        if (asm.CustomAttributes.Any())
                            text.Append("\n");

                        usedAssemblyAttributes.UnionWith(asm.CustomAttributes);
                    }
            }
            return text.ToString().TrimEnd();
        }

        private StringBuilder generateType(TypeInfo type, IEnumerable<string> namespaces, string prefix = "") {
            // Don't output compiler-generated types if desired
            if (MustCompile && type.GetCustomAttributes(CGAttribute).Any())
                return new StringBuilder();

            var codeBlocks = new Dictionary<string, StringBuilder>();
            var usedMethods = new List<MethodInfo>();
            StringBuilder sb;

            var scope = new Scope {
                Current = type,
                Namespaces = namespaces
            };

            // Fields
            sb = new StringBuilder();
            if (!type.IsEnum) {
                foreach (var field in type.DeclaredFields) {
                    if (MustCompile && field.GetCustomAttributes(CGAttribute).Any())
                        continue;

                    // Generate custom field attribute info
                    sb.Append(GenerateCustomFieldAttributeInfo(field, prefix + "\t"));

                    if (field.IsNotSerialized)
                        sb.Append(prefix + "\t[NonSerialized]\n");

                    if (field.IsThreadStatic)
                        sb.Append(prefix + "\t[ThreadStatic]\n");

                    // Attributes
                    sb.Append(field.CustomAttributes.Where(a => a.AttributeType.FullName != FBAttribute).OrderBy(a => a.AttributeType.Name)
                        .ToString(scope, prefix + "\t", emitPointer: !SuppressMetadata, mustCompile: MustCompile));
                    
                    // Add individual attribute info
                    foreach (var ca in field.CustomAttributes.Where(a => a.AttributeType.FullName != FBAttribute))
                        sb.Append(GenerateAttributeAttributeInfo(ca, prefix + "\t"));
                    sb.Append(prefix + "\t");
                    sb.Append(field.GetModifierString());

                    // Fixed buffers
                    if (field.GetCustomAttributes(FBAttribute).Any()) {
                        if (!SuppressMetadata)
                            sb.Append($"/* {field.GetCustomAttributes(FBAttribute)[0].VirtualAddress.ToAddressString()} */ ");
                        sb.Append($"{field.FieldType.DeclaredFields[0].FieldType.GetScopedCSharpName(scope)} {field.CSharpName}[0]"); // FixedElementField
                    }
                    // Regular fields
                    else
                        sb.Append($"{field.FieldType.GetScopedCSharpName(scope)} {field.CSharpName}");
                    if (field.HasDefaultValue)
                        sb.Append($" = {field.GetDefaultValueString(scope)}");
                    sb.Append(";");
                    // Don't output field indices for const fields (they don't have any storage)
                    // or open generic types (they aren't known until runtime)
                    if (!field.IsLiteral && !SuppressMetadata && !type.ContainsGenericParameters)
                        sb.Append($" // 0x{(uint) field.Offset:X2}");
                    // Output metadata file offset for const fields
                    if (field.IsLiteral && !SuppressMetadata)
                        sb.Append($" // Metadata: {field.DefaultValueMetadataAddress.ToAddressString()}");
                    // For static array initializers, output metadata address and preview
                    if (field.HasFieldRVA && !SuppressMetadata) {
                        var preview = model.Package.Metadata.ReadBytes((long) field.DefaultValueMetadataAddress, field.FieldType.Sizes.NativeSize);
                        sb.Append($" // Static value (base64): {Convert.ToBase64String(preview)} - Metadata: {field.DefaultValueMetadataAddress.ToAddressString()}");
                    }
                    sb.Append("\n");
                }
                codeBlocks.Add("Fields", sb);
            }

            // Properties
            sb = new StringBuilder();
            var hasIndexer = false;
            foreach (var prop in type.DeclaredProperties) {

                // Generate custom method attribute info for getter and setter
                if (prop.GetMethod != null)
                    sb.Append(GenerateCustomMethodAttributeInfo(prop.GetMethod, "Getter", prop.Name, prefix + "\t"));
                if (prop.SetMethod != null)
                    sb.Append(GenerateCustomMethodAttributeInfo(prop.SetMethod, "Setter", prop.Name, prefix + "\t"));

                // Attributes
                sb.Append(prop.CustomAttributes.OrderBy(a => a.AttributeType.Name)
                    .ToString(scope, prefix + "\t", emitPointer: !SuppressMetadata, mustCompile: MustCompile));
                
                // Add individual attribute info
                foreach (var ca in prop.CustomAttributes)
                    sb.Append(GenerateAttributeAttributeInfo(ca, prefix + "\t"));

                // The access mask enum values go from 1 (private) to 6 (public) in order from most to least restrictive
                var getAccess = (prop.GetMethod?.Attributes ?? 0) & MethodAttributes.MemberAccessMask;
                var setAccess = (prop.SetMethod?.Attributes ?? 0) & MethodAttributes.MemberAccessMask;

                // In case the access level of both is the same and the selected method is null, pick the other one (rare edge case)
                var primary = (getAccess >= setAccess ? prop.GetMethod : prop.SetMethod) ?? prop.GetMethod ?? prop.SetMethod;
                sb.Append($"{prefix}\t{primary.GetModifierString()}{prop.PropertyType.GetScopedCSharpName(scope)} ");

                // Non-indexer; non-auto-properties should have a body
                var needsBody = MustCompile && !type.IsInterface && !type.IsAbstract && !prop.IsAutoProperty;

                var getBody = needsBody? " => default;" : ";";
                var setBody = needsBody? " {}" : ";";
                if ((!prop.CanRead || !prop.GetMethod.DeclaredParameters.Any()) && (!prop.CanWrite || prop.SetMethod.DeclaredParameters.Count == 1))
                    sb.Append($"{prop.CSharpName} {{ ");
                
                // Indexer
                else {
                    // Replace indexer name (usually "Item" but not always) with "this" - preserves explicit interface implementations
                    if (prop.CSharpName.IndexOf('.') != -1)
                        sb.Append(prop.CSharpName.Substring(0, prop.CSharpName.LastIndexOf('.') + 1));
                    sb.Append("this[" + string.Join(", ", primary.DeclaredParameters.SkipLast(getAccess >= setAccess ? 0 : 1)
                                  .Select(p => p.GetParameterString(scope, !SuppressMetadata, MustCompile))) + "] { ");
                    getBody = " => default;";
                    setBody = " {}";
                    hasIndexer = true;
                }

                sb.Append((prop.CanRead? prop.GetMethod.CustomAttributes.Where(a => !MustCompile || a.AttributeType.FullName != CGAttribute)
                                             .ToString(scope, inline: true, emitPointer: !SuppressMetadata, mustCompile: MustCompile) 
                                               + (getAccess < setAccess? prop.GetMethod.GetAccessModifierString() : "") + $"get{getBody} " : "")
                             // Auto-properties must have get accessors (exclude indexers)
                             + (MustCompile && !prop.CanRead && setBody == ";"? "get; " : "")
                             + (prop.CanWrite? prop.SetMethod.CustomAttributes.Where(a => !MustCompile || a.AttributeType.FullName != CGAttribute)
                                                   .ToString(scope, inline: true, emitPointer: !SuppressMetadata, mustCompile: MustCompile) 
                                               + (setAccess < getAccess? prop.SetMethod.GetAccessModifierString() : "") + $"set{setBody} " : "") + "}");
                if (!SuppressMetadata) {
                    if ((prop.CanRead && prop.GetMethod.VirtualAddress != null) || (prop.CanWrite && prop.SetMethod.VirtualAddress != null))
                        sb.Append(" // ");
                    sb.Append((prop.CanRead && prop.GetMethod.VirtualAddress != null ? prop.GetMethod.VirtualAddress.ToAddressString() + " " : "")
                                + (prop.CanWrite && prop.SetMethod.VirtualAddress != null ? prop.SetMethod.VirtualAddress.ToAddressString() : ""));
                }
                sb.Append("\n");

                usedMethods.Add(prop.GetMethod);
                usedMethods.Add(prop.SetMethod);
            }
            codeBlocks.Add("Properties", sb);

            // Events
            sb = new StringBuilder();
            foreach (var evt in type.DeclaredEvents) {

                // Generate custom method attribute info for event methods
                if (evt.AddMethod != null)
                    sb.Append(GenerateCustomMethodAttributeInfo(evt.AddMethod, "EventAdd", evt.Name, prefix + "\t"));
                if (evt.RemoveMethod != null)
                    sb.Append(GenerateCustomMethodAttributeInfo(evt.RemoveMethod, "EventRemove", evt.Name, prefix + "\t"));
                if (evt.RaiseMethod != null)
                    sb.Append(GenerateCustomMethodAttributeInfo(evt.RaiseMethod, "EventInvoke", evt.Name, prefix + "\t"));

                // Attributes
                sb.Append(evt.CustomAttributes.OrderBy(a => a.AttributeType.Name)
                    .ToString(scope, prefix + "\t", emitPointer: !SuppressMetadata, mustCompile: MustCompile));
                
                // Add individual attribute info
                foreach (var ca in evt.CustomAttributes)
                    sb.Append(GenerateAttributeAttributeInfo(ca, prefix + "\t"));

                string modifiers = evt.AddMethod?.GetModifierString();
                sb.Append($"{prefix}\t{modifiers}event {evt.EventHandlerType.GetScopedCSharpName(scope)} {evt.CSharpName}");
                
                if (!MustCompile) {
                    sb.Append(" {\n");
                    var m = new Dictionary<string, (ulong, ulong)?>();
                    if (evt.AddMethod != null) m.Add("add", evt.AddMethod.VirtualAddress);
                    if (evt.RemoveMethod != null) m.Add("remove", evt.RemoveMethod.VirtualAddress);
                    if (evt.RaiseMethod != null) m.Add("raise", evt.RaiseMethod.VirtualAddress);
                    sb.Append(string.Join("\n", m.Select(x => $"{prefix}\t\t{x.Key};{(SuppressMetadata? "" : " // " + x.Value.ToAddressString())}")) + "\n" + prefix + "\t}\n");
                } else
                    sb.Append(";\n");

                usedMethods.Add(evt.AddMethod);
                usedMethods.Add(evt.RemoveMethod);
                usedMethods.Add(evt.RaiseMethod);
            }
            codeBlocks.Add("Events", sb);

            // Nested types
            codeBlocks.Add("Nested types", new StringBuilder().AppendJoin("\n", type.DeclaredNestedTypes
                .Select(n => generateType(n, namespaces, prefix + "\t")).Where(c => c.Length > 0)));

            // Constructors
            sb = new StringBuilder();
            var fields = type.DeclaredFields.Where(f => !f.GetCustomAttributes(CGAttribute).Any());

            // Crete a parameterless constructor for every relevant type when making code that compiles to mitigate CS1729 and CS7036
            if (MustCompile && !type.IsInterface && !(type.IsAbstract && type.IsSealed) && !type.IsValueType
                && type.DeclaredConstructors.All(c => c.IsStatic || c.DeclaredParameters.Any()))
                sb.Append($"{prefix}\t{(type.IsAbstract? "protected" : "public")} {type.CSharpBaseName}() {{}} // Dummy constructor\n");

            foreach (var method in type.DeclaredConstructors) {
                // Generate custom method attribute info
                sb.Append(GenerateCustomMethodAttributeInfo(method, "Constructor", "", prefix + "\t"));

                // Attributes
                sb.Append(method.CustomAttributes.OrderBy(a => a.AttributeType.Name)
                    .ToString(scope, prefix + "\t", emitPointer: !SuppressMetadata, mustCompile: MustCompile));
                
                // Add individual attribute info
                foreach (var ca in method.CustomAttributes)
                    sb.Append(GenerateAttributeAttributeInfo(ca, prefix + "\t"));

                sb.Append($"{prefix}\t{method.GetModifierString()}{method.DeclaringType.CSharpBaseName}{method.GetTypeParametersString(scope)}");
                sb.Append($"({method.GetParametersString(scope, !SuppressMetadata)})");

                if (MustCompile) {
                    // Class constructor
                    if (method.IsAbstract)
                        sb.Append(";");
                    else if (!type.IsValueType)
                        sb.Append(" {}");

                    // Struct constructor
                    else {
                        // Parameterized struct constructors must call the parameterless constructor to create the object
                        // if the object has any auto-implemented properties
                        if (type.DeclaredProperties.Any() && method.DeclaredParameters.Any())
                            sb.Append(" : this()");

                        // Struct construvctors must initialize all fields in the struct
                        if (fields.Any()) {
                            var paramNames = method.DeclaredParameters.Select(p => p.Name);
                            sb.Append(" {\n" + string.Join("\n", fields
                                            .Where(f => !f.IsLiteral && f.IsStatic == method.IsStatic)
                                            .Select(f => $"{prefix}\t\t{(paramNames.Contains(f.Name) ? "this." : "")}{f.Name} = default;"))
                                        + $"\n{prefix}\t}}");
                        } else
                            sb.Append(" {}");
                    }
                } else
                    sb.Append(";");

                sb.Append((!SuppressMetadata && method.VirtualAddress != null ? $" // {method.VirtualAddress.ToAddressString()}" : "") + "\n");
            }
            codeBlocks.Add("Constructors", sb);

            // Methods
            // Don't re-output methods for constructors, properties, events etc.
            var methods = type.DeclaredMethods.Except(usedMethods).Where(m => m.CustomAttributes.All(a => a.AttributeType.FullName != ExtAttribute));
            codeBlocks.Add("Methods", methods
                .Select(m => generateMethod(m, scope, prefix))
                .Aggregate(new StringBuilder(), (r, i) => r.Append(i)));

            usedMethods.AddRange(methods);

            // Extension methods 
            codeBlocks.Add("Extension methods", type.DeclaredMethods.Except(usedMethods)
                .Select(m => generateMethod(m, scope, prefix))
                .Aggregate(new StringBuilder(), (r, i) => r.Append(i)));

            // Type declaration
            sb = new StringBuilder();

            // Generate custom class attribute info
            sb.Append(GenerateCustomClassAttributeInfo(type, prefix));

            if (type.IsImport)
                sb.Append(prefix + "[ComImport]\n");
            if (type.IsSerializable)
                sb.Append(prefix + "[Serializable]\n");

            // DefaultMemberAttribute should be output if it is present and the type does not have an indexer, otherwise suppressed
            // See https://docs.microsoft.com/en-us/dotnet/api/system.reflection.defaultmemberattribute?view=netframework-4.8
            sb.Append(type.CustomAttributes.Where(a => (a.AttributeType.FullName != DMAttribute || !hasIndexer) && a.AttributeType.FullName != ExtAttribute)
                                            .OrderBy(a => a.AttributeType.Name).ToString(scope, prefix, emitPointer: !SuppressMetadata, mustCompile: MustCompile));
            
            // Add individual attribute info
            foreach (var ca in type.CustomAttributes.Where(a => (a.AttributeType.FullName != DMAttribute || !hasIndexer) && a.AttributeType.FullName != ExtAttribute))
                sb.Append(GenerateAttributeAttributeInfo(ca, prefix));

            // Roll-up multicast delegates to use the 'delegate' syntactic sugar
            if (type.IsClass && type.IsSealed && type.BaseType?.FullName == "System.MulticastDelegate") {
                sb.Append(prefix + type.GetAccessModifierString());

                var del = type.GetMethod("Invoke");
                // IL2CPP doesn't seem to retain return type attributes
                //sb.Append(del.ReturnType.CustomAttributes.ToString(prefix, "return: ", emitPointer: !SuppressMetadata, mustCompile: MustCompile));
                if (del.RequiresUnsafeContext)
                    sb.Append("unsafe ");
                sb.Append($"delegate {del.ReturnType.GetScopedCSharpName(scope)} {type.GetCSharpTypeDeclarationName()}(");
                sb.Append(del.GetParametersString(scope, !SuppressMetadata) + ");");
                if (!SuppressMetadata)
                    sb.Append($" // TypeDefIndex: {type.Index}; {del.VirtualAddress.ToAddressString()}");
                sb.Append("\n");
                return sb;
            }

            sb.Append(prefix + type.GetModifierString());

            var @base = type.NonInheritedInterfaces.Select(x => x.GetScopedCSharpName(scope, isPartOfTypeDeclaration: true)).ToList();
            if (type.BaseType != null && type.BaseType.FullName != "System.Object" && type.BaseType.FullName != "System.ValueType" && !type.IsEnum)
                @base.Insert(0, type.BaseType.GetScopedCSharpName(scope, isPartOfTypeDeclaration: true));
            if (type.IsEnum && type.GetEnumUnderlyingType().FullName != "System.Int32") // enums derive from int by default
                @base.Insert(0, type.GetEnumUnderlyingType().GetScopedCSharpName(scope));
            var baseText = @base.Count > 0 ? " : " + string.Join(", ", @base) : string.Empty;

            sb.Append($"{type.GetCSharpTypeDeclarationName()}{baseText}");
            if (!SuppressMetadata)
                sb.Append($" // TypeDefIndex: {type.Index}");
            sb.Append("\n");

            foreach (var gp in type.GetGenericArguments()) {
                var constraint = gp.GetTypeConstraintsString(scope);
                if (constraint != string.Empty)
                    sb.Append($"{prefix}\t{constraint}\n");
            }

            sb.Append(prefix + "{\n");

            // Enumeration
            if (type.IsEnum) {
                var enumFieldSb = new StringBuilder();
                
                // Generate CustomFieldAttribute for each enum field
                foreach (var field in type.DeclaredFields.Where(f => f.IsLiteral && f.IsStatic)) {
                    enumFieldSb.Append(GenerateCustomFieldAttributeInfo(field, prefix + "\t"));
                }
                
                var enumValues = type.GetEnumNames().Zip(type.GetEnumValues().OfType<object>(),
                              (k, v) => new { k, v }).OrderBy(x => x.v).Select(x => $"{prefix}\t{x.k} = {x.v}");
                
                sb.Append(enumFieldSb.ToString());
                sb.AppendJoin(",\n", enumValues);
                sb.Append("\n");
            }

            // Type definition
            else
                sb.AppendJoin("\n", codeBlocks.Where(b => b.Value.Length > 0).Select(b => prefix + "\t// " + b.Key + "\n" + b.Value));

            sb.Append(prefix + "}\n");
            return sb;
        }

        private StringBuilder generateMethod(MethodInfo method, Scope scope, string prefix) {
            var writer = new StringBuilder();

            if (MustCompile && method.GetCustomAttributes(CGAttribute).Any())
                return writer;

            // Generate custom method attribute info
            writer.Append(GenerateCustomMethodAttributeInfo(method, "Normal", "", prefix + "\t"));

            // Attributes
            writer.Append(method.CustomAttributes.Where(a => a.AttributeType.FullName != ExtAttribute && a.AttributeType.FullName != AsyncAttribute)
                .OrderBy(a => a.AttributeType.Name)
                .ToString(scope, prefix + "\t", emitPointer: !SuppressMetadata, mustCompile: MustCompile));
            
            // Add individual attribute info
            foreach (var ca in method.CustomAttributes.Where(a => a.AttributeType.FullName != ExtAttribute && a.AttributeType.FullName != AsyncAttribute))
                writer.Append(GenerateAttributeAttributeInfo(ca, prefix + "\t"));

            // IL2CPP doesn't seem to retain return type attributes
            //writer.Append(method.ReturnType.CustomAttributes.ToString(prefix + "\t", "return: ", emitPointer: !SuppressMetadata));
            writer.Append($"{prefix}\t{method.GetModifierString()}");

            // Finalizers become destructors
            if (method.Name == "Finalize" && method.IsVirtual && method.ReturnType.FullName == "System.Void" && method.IsFamily)
                writer.Append("~" + method.DeclaringType.CSharpBaseName);
                
            // Regular method or operator overload
            else if (method.Name != "op_Implicit" && method.Name != "op_Explicit")
                writer.Append($"{method.ReturnParameter.GetReturnParameterString(scope)} {method.CSharpName}{method.GetTypeParametersString(scope)}");

            // User-defined conversion operator
            else
                writer.Append($"{method.CSharpName}{method.ReturnType.GetScopedCSharpName(scope)}");

            // Parameters
            writer.Append("(" + method.GetParametersString(scope, !SuppressMetadata) + ")");

            // Generic type constraints
            foreach (var gp in method.GetGenericArguments()) {
                var constraint = gp.GetTypeConstraintsString(scope);
                if (constraint != string.Empty)
                    writer.Append($"\n{prefix}\t\t{constraint}");
            }

            // Body
            var methodBody = MustCompile? method switch {
                    // Abstract method
                    { IsAbstract: true } => ";",

                    // Extern method
                    { Attributes: var a } when (a & MethodAttributes.PinvokeImpl) == MethodAttributes.PinvokeImpl => ";",

                    // Method with out parameters
                    { DeclaredParameters: var d } when d.Any(p => p.IsOut) =>
                        " {\n" + string.Join("\n", d.Where(p => p.IsOut).Select(p => $"{prefix}\t\t{p.Name} = default;"))
                        + (method.ReturnType.FullName != "System.Void"? $"\n{prefix}\t\treturn default;" : "")
                        + $"\n{prefix}\t}}",

                    // No return type
                    { ReturnType: var retType } when retType.FullName == "System.Void" => " {}",

                    // Ref return type
                    { ReturnType: var retType } when retType.IsByRef => " => ref _refReturnTypeFor" + method.CSharpName + ";",

                    // Regular return type
                    _ => " => default;"
                }

                // Only make a method body if we are trying to compile the output
                : ";";

            writer.Append(methodBody + (!SuppressMetadata && method.VirtualAddress != null ? $" // {method.VirtualAddress.ToAddressString()}" : "") + "\n");

            // Ref return type requires us to invent a field
            if (MustCompile && method.ReturnType.IsByRef)
                writer.Append($"{prefix}\tprivate {method.ReturnType.GetScopedCSharpName(scope)} _refReturnTypeFor{method.CSharpName}; // Dummy field\n");

            return writer;
        }
    }
}
