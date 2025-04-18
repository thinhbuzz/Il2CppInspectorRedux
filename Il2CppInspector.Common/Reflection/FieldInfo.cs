/*
    Copyright 2017-2021 Katy Coe - http://www.djkaty.com - https://github.com/djkaty

    All rights reserved.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text;
using Il2CppInspector.Next.Metadata;

namespace Il2CppInspector.Reflection {
    public class FieldInfo : MemberInfo // L-TODO: Add support for [ThreadLocal] fields
    {
        // IL2CPP-specific data
        public Il2CppFieldDefinition Definition { get; }
        public int Index { get; }
        // Root definition: the field with Definition != null
        protected readonly FieldInfo rootDefinition;

        // Offsets for reference types start at 0x8 or 0x10 due to Il2CppObject "header" containing 2 pointers
        // Value types don't have this header but the offsets are still stored as starting at 0x8 or 0x10, so we have to subtract this
        // Open generic types have offsets that aren't known until runtime
        private readonly long rawOffset;
        public long Offset => DeclaringType.ContainsGenericParameters? 0 :
            rawOffset - (DeclaringType.IsValueType && !IsStatic? (Assembly.Model.Package.BinaryImage.Bits / 8) * 2 : 0);

        public bool HasFieldRVA => (Attributes & FieldAttributes.HasFieldRVA) != 0;
        public ulong DefaultValueMetadataAddress { get; }

        public bool IsThreadStatic { get; }

        // Custom attributes for this member
        public override IEnumerable<CustomAttributeData> CustomAttributes => CustomAttributeData.GetCustomAttributes(rootDefinition);

        public bool HasDefaultValue => (Attributes & FieldAttributes.HasDefault) != 0;
        public object DefaultValue { get; }

        public string GetDefaultValueString(Scope usingScope = null) => HasDefaultValue ? DefaultValue.ToCSharpValue(FieldType, usingScope) : "";

        // Information/flags about the field
        public FieldAttributes Attributes { get; }

        // Type of field
        private readonly TypeRef fieldTypeReference;
        public TypeInfo FieldType => fieldTypeReference.Value;

        // For the Is* definitions below, see:
        // https://docs.microsoft.com/en-us/dotnet/api/system.reflection.fieldinfo.isfamilyandassembly?view=netframework-4.7.1#System_Reflection_FieldInfo_IsFamilyAndAssembly

        // True if the field is declared as internal
        public bool IsAssembly => (Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Assembly;

        // True if the field is declared as protected
        public bool IsFamily => (Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Family;

        // True if the field is declared as 'protected private' (always false)
        public bool IsFamilyAndAssembly => (Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.FamANDAssem;

        // True if the field is declared as protected public
        public bool IsFamilyOrAssembly => (Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.FamORAssem;

        // True if the field is declared as readonly
        public bool IsInitOnly => (Attributes & FieldAttributes.InitOnly) == FieldAttributes.InitOnly;

        // True if the field is const
        public bool IsLiteral => (Attributes & FieldAttributes.Literal) == FieldAttributes.Literal;

        // True if the field has the NonSerialized attribute
#pragma warning disable SYSLIB0050
        public bool IsNotSerialized => (Attributes & FieldAttributes.NotSerialized) == FieldAttributes.NotSerialized;
#pragma warning restore SYSLIB0050

        // True if the field is extern
        public bool IsPinvokeImpl => (Attributes & FieldAttributes.PinvokeImpl) == FieldAttributes.PinvokeImpl;

        // True if the field is declared a private
        public bool IsPrivate => (Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Private;

        // True if the field is declared as public
        public bool IsPublic => (Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Public;

        // True if the field has a special name
        public bool IsSpecialName => (Attributes & FieldAttributes.SpecialName) == FieldAttributes.SpecialName;

        // True if the field is declared as static
        public bool IsStatic => (Attributes & FieldAttributes.Static) == FieldAttributes.Static;

        // Returns true if using this field requires that the using method is declared as unsafe
        public bool RequiresUnsafeContext => FieldType.RequiresUnsafeContext || GetCustomAttributes("System.Runtime.CompilerServices.FixedBufferAttribute").Any();

        public override MemberTypes MemberType => MemberTypes.Field;

        public FieldInfo(Il2CppInspector pkg, int fieldIndex, TypeInfo declaringType) :
            base(declaringType) {
            Definition = pkg.Fields[fieldIndex];
            MetadataToken = (int) Definition.Token;
            Index = fieldIndex;
            Name = pkg.Strings[Definition.NameIndex];

            rawOffset = pkg.FieldOffsets[fieldIndex];
            if (0 > rawOffset)
            {
                IsThreadStatic = true;
                rawOffset &= ~0x80000000;
            }

            rootDefinition = this;

            fieldTypeReference = TypeRef.FromReferenceIndex(Assembly.Model, Definition.TypeIndex);
            var fieldType = pkg.TypeReferences[Definition.TypeIndex];

            // Copy attributes
            Attributes = (FieldAttributes) fieldType.Attrs;

            // Default initialization value if present
            if (pkg.FieldDefaultValue.TryGetValue(fieldIndex, out (ulong address, object variant) value)) {
                DefaultValue = value.variant;
                DefaultValueMetadataAddress = value.address;
            }
        }

        public FieldInfo(FieldInfo fieldDef, TypeInfo declaringType) : base(declaringType) {
            if (!fieldDef.Definition.IsValid)
                throw new ArgumentException("Argument must be a bare field definition");

            rootDefinition = fieldDef;

            Name = fieldDef.Name;
            Attributes = fieldDef.Attributes;
            fieldTypeReference = TypeRef.FromTypeInfo(fieldDef.FieldType.SubstituteGenericArguments(declaringType.GetGenericArguments()));

            DefaultValue = fieldDef.DefaultValue;
            DefaultValueMetadataAddress = fieldDef.DefaultValueMetadataAddress;
        }

        public string GetAccessModifierString()
        {
            var accessModifier = GetAccessModifierStringRaw();
            if (accessModifier == "")
            {
                return accessModifier;
            }
            return accessModifier + " ";
        }

        public string GetAccessModifierStringRaw() => this switch
        {
            { IsPrivate: true } => "private",
            { IsPublic: true } => "public",
            { IsFamily: true } => "protected",
            { IsAssembly: true } => "internal",
            { IsFamilyOrAssembly: true } => "protected internal",
            { IsFamilyAndAssembly: true } => "private protected",
            _ => ""
        };

        public string GetModifierString()
        {
            var modifiers = GetModifierStringRaw();

            if (modifiers.Count == 0)
            {
                return "";
            }

            modifiers.Prepend(GetAccessModifierString());
            modifiers.Add("");

            return string.Join(" ", modifiers);
        }

        public List<string> GetModifierStringRaw()
        {
            List<string> modifiers = new List<string>();

            if (IsLiteral)
                modifiers.Add("const");
            // All const fields are also static by implication
            else if (IsStatic)
                modifiers.Add("static");
            if (IsInitOnly)
                modifiers.Add("readonly");
            if (RequiresUnsafeContext)
                modifiers.Add("unsafe");
            if (IsPinvokeImpl)
                modifiers.Add("extern");
            if (GetCustomAttributes("System.Runtime.CompilerServices.FixedBufferAttribute").Any())
                modifiers.Add("fixed");
            return modifiers;
        }
    }
}