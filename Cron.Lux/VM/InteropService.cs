﻿using System;
using System.Collections.Generic;
using System.Linq;

namespace Cron.Lux.VM
{
    public class InteropCall
    {
        public string name;
        public Func<ExecutionEngine, bool> handler;
        public decimal gasCost;
    }

    public class InteropService
    {
        public const decimal defaultGasCost = 0.001m;

        private Dictionary<string, InteropCall> dictionary = new Dictionary<string, InteropCall>();

        public IEnumerable<InteropCall> Calls => dictionary.Values;

        public InteropService()
        {
            Register("System.ExecutionEngine.GetScriptContainer", GetScriptContainer, defaultGasCost);
            Register("System.ExecutionEngine.GetExecutingScriptHash", GetExecutingScriptHash, defaultGasCost);
            Register("System.ExecutionEngine.GetCallingScriptHash", GetCallingScriptHash, defaultGasCost);
            Register("System.ExecutionEngine.GetEntryScriptHash", GetEntryScriptHash, defaultGasCost);
        }

        public InteropCall FindCall(string method)
        {
            if (!dictionary.ContainsKey(method))
            {
                return null;
            }

            return dictionary[method];
        }

        public void Register(string method, Func<ExecutionEngine, bool> handler, decimal gasCost)
        {
            var call = new InteropCall();
            call.handler = handler;
            call.gasCost = gasCost;
            call.name = method;
            dictionary[method] = call;
        }

        internal bool Invoke(string method, ExecutionEngine engine)
        {
            if (!dictionary.ContainsKey(method))
            {
                return false;
            }
            return dictionary[method].handler(engine);
        }

        internal static bool GetScriptContainer(ExecutionEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push(StackItem.FromInterface(engine.ScriptContainer));
            return true;
        }

        internal static bool GetExecutingScriptHash(ExecutionEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push(engine.CurrentContext.ScriptHash.ToArray());
            return true;
        }

        internal static bool GetCallingScriptHash(ExecutionEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push(engine.CallingContext.ScriptHash.ToArray());
            return true;
        }

        internal static bool GetEntryScriptHash(ExecutionEngine engine)
        {
            engine.CurrentContext.EvaluationStack.Push(engine.EntryContext.ScriptHash.ToArray());
            return true;
        }
    }
}
