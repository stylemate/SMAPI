using System;
using System.Collections.Generic;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using StardewModdingAPI.Framework.ModLoading.Framework;

namespace StardewModdingAPI.Framework.ModLoading.Rewriters
{
    /// <summary>Rewrites references to one field with another.</summary>
    internal class FieldReplaceRewriter : BaseInstructionHandler
    {
        /*********
        ** Fields
        *********/
        /// <summary>The new fields to reference indexed by the old field/type names.</summary>
        private readonly Dictionary<string, Dictionary<string, FieldInfo>> FieldMaps = new();


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        public FieldReplaceRewriter()
            : base(defaultPhrase: "field replacement") { } // will be overridden when a field is replaced

        /// <summary>Add a field to replace.</summary>
        /// <param name="fromType">The type whose field to rewrite.</param>
        /// <param name="fromFieldName">The field name to rewrite.</param>
        /// <param name="toType">The new type which will have the field.</param>
        /// <param name="toFieldName">The new field name to reference.</param>
        public FieldReplaceRewriter AddField(Type fromType, string fromFieldName, Type toType, string toFieldName)
        {
            return this.AddField(
                fromType?.FullName!, // will be validated in the called method
                fromFieldName,
                toType,
                toFieldName
            );
        }

        /// <summary>Add a field to replace.</summary>
        /// <param name="fromTypeFullName">The full name of the type whose field to rewrite.</param>
        /// <param name="fromFieldName">The field name to rewrite.</param>
        /// <param name="toType">The new type which will have the field.</param>
        /// <param name="toFieldName">The new field name to reference.</param>
        public FieldReplaceRewriter AddField(string fromTypeFullName, string fromFieldName, Type toType, string toFieldName)
        {
            // validate parameters
            if (string.IsNullOrWhiteSpace(fromTypeFullName))
                throw new InvalidOperationException("Can't replace a field on a null source type.");
            if (toType == null)
                throw new InvalidOperationException("Can't replace a field on a null target type.");

            // get target field
            FieldInfo? toField = toType.GetField(toFieldName);
            if (toField == null)
                throw new InvalidOperationException($"The {toType.FullName} class doesn't have a {toFieldName} field.");

            // add mapping
            if (!this.FieldMaps.TryGetValue(fromTypeFullName, out var fieldMap))
                this.FieldMaps[fromTypeFullName] = fieldMap = new();
            fieldMap[fromFieldName] = toField;

            return this;
        }

        /// <inheritdoc />
        public override bool Handle(ModuleDefinition module, ILProcessor cil, Instruction instruction)
        {
            FieldReference? fieldRef = RewriteHelper.AsFieldReference(instruction);
            string? declaringType = fieldRef?.DeclaringType?.FullName;

            // get mapped field
            if (declaringType == null || !this.FieldMaps.TryGetValue(declaringType, out var fieldMap) || !fieldMap.TryGetValue(fieldRef!.Name, out FieldInfo? toField))
                return false;

            // replace with new field
            this.Phrases.Add($"{fieldRef.DeclaringType!.Name}.{fieldRef.Name} field");
            instruction.Operand = module.ImportReference(toField);
            return this.MarkRewritten();
        }
    }
}
