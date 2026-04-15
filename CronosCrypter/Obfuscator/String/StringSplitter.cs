using dnlib.DotNet;
using dnlib.DotNet.Emit;
using System;
using System.Collections.Generic;

namespace CronosCrypter.Obfuscator.String
{
    internal class StringSplitter
    {
        private const int ChunkSize = 3;

        public static void Execute(ModuleDef module)
        {
            if (module == null)
            {
                return;
            }

            IMethod concatArrayMethod = module.Import(
                typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(string[]) })
            );

            foreach (TypeDef type in module.Types)
            {
                ProcessType(type, concatArrayMethod);
            }
        }

        private static void ProcessType(TypeDef type, IMethod concatArrayMethod)
        {
            foreach (MethodDef method in type.Methods)
            {
                if (!method.HasBody)
                {
                    continue;
                }

                ProcessMethod(method, concatArrayMethod);
            }

            foreach (TypeDef nestedType in type.NestedTypes)
            {
                ProcessType(nestedType, concatArrayMethod);
            }
        }

        private static void ProcessMethod(MethodDef method, IMethod concatArrayMethod)
        {
            CilBody body = method.Body;
            body.SimplifyBranches();
            body.SimplifyMacros(method.Parameters);

            for (int i = 0; i < body.Instructions.Count; i++)
            {
                Instruction originalInstruction = body.Instructions[i];
                if (originalInstruction.OpCode != OpCodes.Ldstr)
                {
                    continue;
                }

                string originalString = originalInstruction.Operand as string;
                if (string.IsNullOrEmpty(originalString) || originalString.Length <= ChunkSize)
                {
                    continue;
                }

                string[] chunks = SplitToChunks(originalString, ChunkSize);
                List<Instruction> replacement = CreateConcatArrayInstructions(chunks, concatArrayMethod, method.Module);
                if (replacement.Count == 0)
                {
                    continue;
                }

                // Keep the original instruction instance so any existing branch targets remain valid.
                originalInstruction.OpCode = replacement[0].OpCode;
                originalInstruction.Operand = replacement[0].Operand;

                for (int j = 1; j < replacement.Count; j++)
                {
                    body.Instructions.Insert(i + j, replacement[j]);
                }

                i += replacement.Count - 1;
            }

            // Ensure dnlib metadata (including offsets) is synchronized after instruction edits.
            body.UpdateInstructionOffsets();
            body.OptimizeBranches();
            body.OptimizeMacros();
        }

        private static string[] SplitToChunks(string input, int chunkSize)
        {
            int chunkCount = (input.Length + chunkSize - 1) / chunkSize;
            string[] parts = new string[chunkCount];

            for (int i = 0; i < chunkCount; i++)
            {
                int start = i * chunkSize;
                int length = Math.Min(chunkSize, input.Length - start);
                parts[i] = input.Substring(start, length);
            }

            return parts;
        }

        private static List<Instruction> CreateConcatArrayInstructions(string[] parts, IMethod concatArrayMethod, ModuleDef module)
        {
            var instructions = new List<Instruction>();
            instructions.Add(Instruction.Create(OpCodes.Ldc_I4, parts.Length));
            instructions.Add(Instruction.Create(OpCodes.Newarr, module.CorLibTypes.String.ToTypeDefOrRef()));

            for (int i = 0; i < parts.Length; i++)
            {
                instructions.Add(Instruction.Create(OpCodes.Dup));
                instructions.Add(Instruction.Create(OpCodes.Ldc_I4, i));
                instructions.Add(Instruction.Create(OpCodes.Ldstr, parts[i]));
                instructions.Add(Instruction.Create(OpCodes.Stelem_Ref));
            }

            instructions.Add(Instruction.Create(OpCodes.Call, concatArrayMethod));
            return instructions;
        }
    }
}
