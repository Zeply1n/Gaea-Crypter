using dnlib.DotNet;
using dnlib.DotNet.Emit;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CronosCrypter.Obfuscator.ControlFlow
{
    internal class ControlFlowFlattener
    {
        public static void Execute(ModuleDefMD module)
        {
            if (module?.EntryPoint == null)
            {
                return;
            }

            FlattenMethod(module.EntryPoint);
        }

        private static void FlattenMethod(MethodDef method)
        {
            if (!method.HasBody || method.Body.Instructions.Count < 3)
            {
                return;
            }

            if (method.Body.HasExceptionHandlers)
            {
                return;
            }

            if (method.Body.Instructions.Any(instruction => instruction.OpCode == OpCodes.Switch))
            {
                return;
            }

            method.Body.SimplifyBranches();
            method.Body.SimplifyMacros(method.Parameters);

            List<Block> blocks = BuildBlocks(method.Body.Instructions);
            if (blocks.Count < 2)
            {
                return;
            }

            Dictionary<Instruction, int> blockLookup = blocks
                .Select((block, index) => new { block, index })
                .ToDictionary(item => item.block.Instructions[0], item => item.index);

            var state = new Local(method.Module.CorLibTypes.Int32);
            method.Body.Variables.Add(state);
            method.Body.InitLocals = true;

            Instruction switchStart = Instruction.Create(OpCodes.Nop);
            Instruction switchInstruction = Instruction.Create(OpCodes.Switch, new Instruction[blocks.Count]);

            var newInstructions = new List<Instruction>
            {
                Instruction.Create(OpCodes.Ldc_I4, 0),
                Instruction.Create(OpCodes.Stloc, state),
                Instruction.Create(OpCodes.Br, switchStart),
                switchStart,
                Instruction.Create(OpCodes.Ldloc, state),
                switchInstruction
            };

            var blockOrder = Enumerable.Range(0, blocks.Count).ToList();
            Shuffle(blockOrder);

            var blockLabels = new Instruction[blocks.Count];
            for (int i = 0; i < blocks.Count; i++)
            {
                blockLabels[i] = Instruction.Create(OpCodes.Nop);
            }

            switchInstruction.Operand = blockLabels;

            foreach (int index in blockOrder)
            {
                Block block = blocks[index];
                newInstructions.Add(blockLabels[index]);

                for (int i = 0; i < block.Instructions.Count - 1; i++)
                {
                    newInstructions.Add(block.Instructions[i]);
                }

                Instruction last = block.LastInstruction;
                if (last == null)
                {
                    continue;
                }

                if (last.OpCode.FlowControl == FlowControl.Return || last.OpCode.FlowControl == FlowControl.Throw)
                {
                    newInstructions.Add(last);
                    continue;
                }

                if (last.OpCode.FlowControl == FlowControl.Branch)
                {
                    if (!TryGetTargetIndex(blockLookup, last.Operand, out int targetIndex))
                    {
                        return;
                    }

                    newInstructions.Add(Instruction.Create(OpCodes.Ldc_I4, targetIndex));
                    newInstructions.Add(Instruction.Create(OpCodes.Stloc, state));
                    newInstructions.Add(Instruction.Create(OpCodes.Br, switchStart));
                    continue;
                }

                if (last.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                    if (!TryGetTargetIndex(blockLookup, last.Operand, out int trueIndex))
                    {
                        return;
                    }

                    if (!TryGetFallThroughIndex(blocks, index, out int falseIndex))
                    {
                        return;
                    }

                    Instruction trueLabel = Instruction.Create(OpCodes.Nop);
                    newInstructions.Add(Instruction.Create(last.OpCode, trueLabel));
                    newInstructions.Add(Instruction.Create(OpCodes.Ldc_I4, falseIndex));
                    newInstructions.Add(Instruction.Create(OpCodes.Stloc, state));
                    newInstructions.Add(Instruction.Create(OpCodes.Br, switchStart));
                    newInstructions.Add(trueLabel);
                    newInstructions.Add(Instruction.Create(OpCodes.Ldc_I4, trueIndex));
                    newInstructions.Add(Instruction.Create(OpCodes.Stloc, state));
                    newInstructions.Add(Instruction.Create(OpCodes.Br, switchStart));
                    continue;
                }

                if (TryGetFallThroughIndex(blocks, index, out int nextIndex))
                {
                    newInstructions.Add(Instruction.Create(OpCodes.Ldc_I4, nextIndex));
                    newInstructions.Add(Instruction.Create(OpCodes.Stloc, state));
                    newInstructions.Add(Instruction.Create(OpCodes.Br, switchStart));
                }
                else
                {
                    newInstructions.Add(last);
                }
            }

            method.Body.Instructions.Clear();
            foreach (Instruction instruction in newInstructions)
            {
                method.Body.Instructions.Add(instruction);
            }

            method.Body.OptimizeBranches();
            method.Body.OptimizeMacros();
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

        private static bool TryGetFallThroughIndex(IReadOnlyList<Block> blocks, int index, out int nextIndex)
        {
            if (index + 1 < blocks.Count)
            {
                nextIndex = index + 1;
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

            public Instruction LastInstruction => Instructions.Count > 0 ? Instructions[Instructions.Count - 1] : null;
        }
    }
}
