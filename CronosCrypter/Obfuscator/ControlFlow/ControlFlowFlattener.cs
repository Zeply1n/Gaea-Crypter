using dnlib.DotNet;
using dnlib.DotNet.Emit;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CronosCrypter.Obfuscator.ControlFlow
{
    internal class ControlFlowFlattener
    {
        private const int StateOffset = 11;
        private const int StateMultiplier = 7;
        private const int StateXorMask = unchecked((int)0x5A5A5A5A);

        public static void Execute(ModuleDefMD module)
        {
            if (module == null)
            {
                return;
            }

            foreach (TypeDef type in EnumerateAllTypes(module.Types))
            {
                foreach (MethodDef method in type.Methods)
                {
                    if (method == null || method.IsAbstract || !method.HasBody)
                    {
                        continue;
                    }

                    FlattenMethod(method);
                }
            }
        }

        private static IEnumerable<TypeDef> EnumerateAllTypes(IList<TypeDef> types)
        {
            foreach (TypeDef type in types)
            {
                yield return type;
                foreach (TypeDef nested in EnumerateAllTypes(type.NestedTypes))
                {
                    yield return nested;
                }
            }
        }

        private static void FlattenMethod(MethodDef method)
        {
            if (method.Body.Instructions.Count < 3)
            {
                return;
            }

            method.Body.SimplifyBranches();
            method.Body.SimplifyMacros(method.Parameters);
            ExpandSwitchInstructions(method);

            List<Block> blocks = BuildBlocks(method.Body.Instructions);
            if (blocks.Count < 2)
            {
                return;
            }

            var blockLookup = blocks
                .Select((block, index) => new { block, index })
                .ToDictionary(item => item.block.Instructions[0], item => item.index);

            var state = new Local(method.Module.CorLibTypes.Int32);
            method.Body.Variables.Add(state);
            method.Body.InitLocals = true;

            Instruction dispatchLabel = Instruction.Create(OpCodes.Nop);
            Instruction switchInstruction = Instruction.Create(OpCodes.Switch, new Instruction[blocks.Count]);

            var newInstructions = new List<Instruction>();
            AppendSetStateInstructions(newInstructions, state, 0);
            newInstructions.Add(Instruction.Create(OpCodes.Br, dispatchLabel));

            newInstructions.Add(dispatchLabel);
            AppendDecodeStateInstructions(newInstructions, state);
            newInstructions.Add(switchInstruction);

            var blockOrder = Enumerable.Range(0, blocks.Count).ToList();
            Shuffle(blockOrder);

            var blockLabels = new Instruction[blocks.Count];
            for (int i = 0; i < blocks.Count; i++)
            {
                blockLabels[i] = Instruction.Create(OpCodes.Nop);
            }

            switchInstruction.Operand = blockLabels;

            var instructionRemap = new Dictionary<Instruction, Instruction>();

            foreach (int blockIndex in blockOrder)
            {
                Block block = blocks[blockIndex];
                newInstructions.Add(blockLabels[blockIndex]);

                for (int i = 0; i < block.Instructions.Count - 1; i++)
                {
                    Instruction original = block.Instructions[i];
                    newInstructions.Add(original);
                    instructionRemap[original] = original;
                }

                Instruction last = block.LastInstruction;
                if (last == null)
                {
                    continue;
                }

                if (last.OpCode.FlowControl == FlowControl.Return ||
                    last.OpCode.FlowControl == FlowControl.Throw ||
                    last.OpCode.Code == Code.Leave ||
                    last.OpCode.Code == Code.Leave_S)
                {
                    newInstructions.Add(last);
                    instructionRemap[last] = last;
                    continue;
                }

                int emittedStart = newInstructions.Count;

                if (last.OpCode.FlowControl == FlowControl.Branch)
                {
                    if (!TryGetTargetIndex(blockLookup, last.Operand, out int targetIndex))
                    {
                        return;
                    }

                    AppendSetStateInstructions(newInstructions, state, targetIndex);
                    newInstructions.Add(Instruction.Create(OpCodes.Br, dispatchLabel));
                    instructionRemap[last] = newInstructions[emittedStart];
                    continue;
                }

                if (last.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                    if (!TryGetTargetIndex(blockLookup, last.Operand, out int trueIndex))
                    {
                        return;
                    }

                    if (!TryGetFallThroughIndex(blocks, blockIndex, out int falseIndex))
                    {
                        return;
                    }

                    Instruction trueLabel = Instruction.Create(OpCodes.Nop);
                    newInstructions.Add(Instruction.Create(last.OpCode, trueLabel));
                    AppendSetStateInstructions(newInstructions, state, falseIndex);
                    newInstructions.Add(Instruction.Create(OpCodes.Br, dispatchLabel));
                    newInstructions.Add(trueLabel);
                    AppendSetStateInstructions(newInstructions, state, trueIndex);
                    newInstructions.Add(Instruction.Create(OpCodes.Br, dispatchLabel));
                    instructionRemap[last] = newInstructions[emittedStart];
                    continue;
                }

                if (TryGetFallThroughIndex(blocks, blockIndex, out int nextIndex))
                {
                    AppendSetStateInstructions(newInstructions, state, nextIndex);
                    newInstructions.Add(Instruction.Create(OpCodes.Br, dispatchLabel));
                    instructionRemap[last] = newInstructions[emittedStart];
                }
                else
                {
                    newInstructions.Add(last);
                    instructionRemap[last] = last;
                }
            }

            RewriteInstructionTargets(newInstructions, instructionRemap);
            RemapExceptionHandlers(method.Body.ExceptionHandlers, instructionRemap);

            method.Body.Instructions.Clear();
            foreach (Instruction instruction in newInstructions)
            {
                method.Body.Instructions.Add(instruction);
            }

            method.Body.UpdateInstructionOffsets();
            method.Body.OptimizeBranches();
            method.Body.OptimizeMacros();
        }

        private static void ExpandSwitchInstructions(MethodDef method)
        {
            var body = method.Body;
            Local switchLocal = null;

            for (int i = 0; i < body.Instructions.Count; i++)
            {
                Instruction instruction = body.Instructions[i];
                if (instruction.OpCode != OpCodes.Switch)
                {
                    continue;
                }

                var targets = instruction.Operand as Instruction[];
                if (targets == null)
                {
                    continue;
                }

                if (switchLocal == null)
                {
                    switchLocal = new Local(method.Module.CorLibTypes.Int32);
                    body.Variables.Add(switchLocal);
                    body.InitLocals = true;
                }

                Instruction defaultTarget = i + 1 < body.Instructions.Count
                    ? body.Instructions[i + 1]
                    : Instruction.Create(OpCodes.Nop);

                var replacement = new List<Instruction>
                {
                    Instruction.Create(OpCodes.Stloc, switchLocal)
                };

                for (int t = 0; t < targets.Length; t++)
                {
                    replacement.Add(Instruction.Create(OpCodes.Ldloc, switchLocal));
                    replacement.Add(Instruction.Create(OpCodes.Ldc_I4, t));
                    replacement.Add(Instruction.Create(OpCodes.Beq, targets[t]));
                }

                replacement.Add(Instruction.Create(OpCodes.Br, defaultTarget));

                Instruction firstReplacement = replacement[0];
                ReplaceInstructionAndRemapReferences(body, instruction, replacement, firstReplacement);
                i += replacement.Count - 1;
            }
        }

        private static void ReplaceInstructionAndRemapReferences(CilBody body, Instruction original, IList<Instruction> replacement, Instruction replacementTarget)
        {
            int index = body.Instructions.IndexOf(original);
            if (index < 0)
            {
                return;
            }

            body.Instructions.RemoveAt(index);
            for (int i = 0; i < replacement.Count; i++)
            {
                body.Instructions.Insert(index + i, replacement[i]);
            }

            foreach (Instruction instruction in body.Instructions)
            {
                if (instruction.Operand is Instruction target && target == original)
                {
                    instruction.Operand = replacementTarget;
                }
                else if (instruction.Operand is Instruction[] targets)
                {
                    for (int i = 0; i < targets.Length; i++)
                    {
                        if (targets[i] == original)
                        {
                            targets[i] = replacementTarget;
                        }
                    }
                }
            }

            foreach (ExceptionHandler handler in body.ExceptionHandlers)
            {
                if (handler.TryStart == original) handler.TryStart = replacementTarget;
                if (handler.TryEnd == original) handler.TryEnd = replacementTarget;
                if (handler.HandlerStart == original) handler.HandlerStart = replacementTarget;
                if (handler.HandlerEnd == original) handler.HandlerEnd = replacementTarget;
                if (handler.FilterStart == original) handler.FilterStart = replacementTarget;
            }
        }

        private static void RewriteInstructionTargets(IEnumerable<Instruction> instructions, IReadOnlyDictionary<Instruction, Instruction> remap)
        {
            foreach (Instruction instruction in instructions)
            {
                if (instruction.Operand is Instruction target && remap.TryGetValue(target, out Instruction mappedTarget))
                {
                    instruction.Operand = mappedTarget;
                }
                else if (instruction.Operand is Instruction[] targets)
                {
                    for (int i = 0; i < targets.Length; i++)
                    {
                        if (remap.TryGetValue(targets[i], out Instruction mappedBranchTarget))
                        {
                            targets[i] = mappedBranchTarget;
                        }
                    }
                }
            }
        }

        private static void RemapExceptionHandlers(IEnumerable<ExceptionHandler> handlers, IReadOnlyDictionary<Instruction, Instruction> remap)
        {
            foreach (ExceptionHandler handler in handlers)
            {
                if (handler.TryStart != null && remap.TryGetValue(handler.TryStart, out Instruction tryStart))
                {
                    handler.TryStart = tryStart;
                }

                if (handler.TryEnd != null && remap.TryGetValue(handler.TryEnd, out Instruction tryEnd))
                {
                    handler.TryEnd = tryEnd;
                }

                if (handler.HandlerStart != null && remap.TryGetValue(handler.HandlerStart, out Instruction handlerStart))
                {
                    handler.HandlerStart = handlerStart;
                }

                if (handler.HandlerEnd != null && remap.TryGetValue(handler.HandlerEnd, out Instruction handlerEnd))
                {
                    handler.HandlerEnd = handlerEnd;
                }

                if (handler.FilterStart != null && remap.TryGetValue(handler.FilterStart, out Instruction filterStart))
                {
                    handler.FilterStart = filterStart;
                }
            }
        }

        private static void AppendSetStateInstructions(List<Instruction> instructions, Local state, int nextState)
        {
            int encoded = EncodeState(nextState);
            instructions.Add(Instruction.Create(OpCodes.Ldc_I4, encoded));
            instructions.Add(Instruction.Create(OpCodes.Stloc, state));
        }

        private static void AppendDecodeStateInstructions(List<Instruction> instructions, Local state)
        {
            instructions.Add(Instruction.Create(OpCodes.Ldloc, state));
            instructions.Add(Instruction.Create(OpCodes.Ldc_I4, StateXorMask));
            instructions.Add(Instruction.Create(OpCodes.Xor));
            instructions.Add(Instruction.Create(OpCodes.Ldc_I4, StateMultiplier));
            instructions.Add(Instruction.Create(OpCodes.Div));
            instructions.Add(Instruction.Create(OpCodes.Ldc_I4, StateOffset));
            instructions.Add(Instruction.Create(OpCodes.Sub));
        }

        private static int EncodeState(int nextState)
        {
            return ((nextState + StateOffset) * StateMultiplier) ^ StateXorMask;
        }

        private static List<Block> BuildBlocks(IList<Instruction> instructions)
        {
            var leaders = new HashSet<Instruction> { instructions[0] };

            for (int i = 0; i < instructions.Count; i++)
            {
                Instruction instruction = instructions[i];

                if (instruction.Operand is Instruction target)
                {
                    leaders.Add(target);
                }
                else if (instruction.Operand is Instruction[] targets)
                {
                    foreach (Instruction switchTarget in targets)
                    {
                        leaders.Add(switchTarget);
                    }
                }

                if (instruction.OpCode.FlowControl == FlowControl.Cond_Branch && i + 1 < instructions.Count)
                {
                    leaders.Add(instructions[i + 1]);
                }
            }

            var blocks = new List<Block>();
            Block current = null;

            foreach (Instruction instruction in instructions)
            {
                if (leaders.Contains(instruction))
                {
                    current = new Block();
                    blocks.Add(current);
                }

                current?.Instructions.Add(instruction);
            }

            return blocks;
        }

        private static bool TryGetTargetIndex(Dictionary<Instruction, int> blockLookup, object operand, out int index)
        {
            if (operand is Instruction target && blockLookup.TryGetValue(target, out index))
            {
                return true;
            }

            index = -1;
            return false;
        }

        private static bool TryGetFallThroughIndex(IReadOnlyList<Block> blocks, int blockIndex, out int nextIndex)
        {
            if (blockIndex + 1 < blocks.Count)
            {
                nextIndex = blockIndex + 1;
                return true;
            }

            nextIndex = -1;
            return false;
        }

        private static void Shuffle(IList<int> list)
        {
            var random = new Random();
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                int temp = list[i];
                list[i] = list[j];
                list[j] = temp;
            }
        }

        private sealed class Block
        {
            public List<Instruction> Instructions { get; } = new List<Instruction>();

            public Instruction LastInstruction => Instructions.Count > 0
                ? Instructions[Instructions.Count - 1]
                : null;
        }
    }
}
