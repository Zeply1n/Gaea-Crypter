using CronosCrypter.Obfuscator.Class;
using CronosCrypter.Obfuscator.ControlFlow;
using CronosCrypter.Obfuscator.String;
using dnlib.DotNet;
using System;
using System.Diagnostics;

namespace CronosCrypter.Obfuscator
{
    internal class Obfuscate
    {
        public void Execute(ModuleDefMD module)
        {
            if (module == null)
            {
                return;
            }

            ExecuteStep("StringSplitter", () => StringSplitter.Execute(module));
            ExecuteStep("ClassRandomization", () => ClassRandomization.Execute(module));
            ExecuteStep("ClassIncreaser", () => ClassIncreaser.Execute(module));
            ExecuteStep("ControlFlowFlattener", () => ControlFlowFlattener.Execute(module));
        }

        private static void ExecuteStep(string name, Action action)
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Obfuscation step '{name}' failed: {ex}");
            }
        }
    }
}
