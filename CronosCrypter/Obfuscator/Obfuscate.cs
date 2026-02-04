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
            StringSplitter.Execute(module);
            ClassRandomization.Execute(module);
            ClassIncreaser.Execute(module);
            ControlFlowFlattener.Execute(module);
        }
    }
}
