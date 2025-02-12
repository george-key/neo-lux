﻿using LunarLabs.Parser;
using Cron.Lux.Cryptography;
using Cron.Lux.Utils;
using System;
using System.Collections.Generic;
using System.IO;

namespace Cron.Lux.Core
{
    public abstract class CronRPC : CronAPI
    {
        public readonly string _cronExplorerUrl;

        public CronRPC(string cronExplorerURL)
        {
            this._cronExplorerUrl = cronExplorerURL;
        }

        public static CronRPC ForMainNet(CronNodesKind kind = CronNodesKind.CRON_GLOBAL)
        {
            throw new Exception("no public CRON JSON-RPC published yet");
          //  return new RemoteRPCNode(<port>, "<cron-node-url>", kind);
        }

       /* public static NeoRPC ForTestNet()
        {
            return new RemoteRPCNode(20332, "https://neoscan-testnet.io", CronNodesKind.CRON_GLOBAL);
        }*/

        public static CronRPC ForPrivateNet()
        {
            return new LocalRPCNode(30333, "http://localhost:4000");
        }

        #region RPC API
        public string rpcEndpoint { get; set; }

        protected abstract string GetRPCEndpoint();

        private void LogData(DataNode node, int ident = 0)
        {
            var tabs = new string('\t', ident);
            Logger($"{tabs}{node}");
            foreach (DataNode child in node.Children)
                LogData(child, ident + 1);
        }

        public DataNode QueryRPC(string method, object[] _params, int id = 1)
        {
            var paramData = DataNode.CreateArray("params");
            foreach (var entry in _params)
            {
                paramData.AddField(null, entry);
            }

            var jsonRpcData = DataNode.CreateObject(null);
            jsonRpcData.AddField("jsonrpc", "2.0");
            jsonRpcData.AddField("method", method);
            jsonRpcData.AddNode(paramData);
            jsonRpcData.AddField("id", id);

            Logger("QueryRPC: " + method);
            LogData(jsonRpcData);

            int retryCount = 0;
            do
            {
                if (rpcEndpoint == null)
                {
                    rpcEndpoint = GetRPCEndpoint();
                    Logger("Update RPC Endpoint: " + rpcEndpoint);
                }

                var response = RequestUtils.Request(RequestType.POST, rpcEndpoint, jsonRpcData);

                if (response != null && response.HasNode("result"))
                {
                    return response;
                }
                else
                {
                    if (response != null && response.HasNode("error"))
                    {
                        var error = response["error"];
                        Logger("RPC Error: " + error.GetString("message"));
                    }
                    else
                    {
                        Logger("No answer");
                    }
                    rpcEndpoint = null;
                    retryCount++;
                }

            } while (retryCount < 10);

            return null;
        }
        #endregion

        public override Dictionary<string, decimal> GetAssetBalancesOf(UInt160 scriptHash)
        {
            var response = QueryRPC("getaccountstate", new object[] { scriptHash.ToAddress() });
            var result = new Dictionary<string, decimal>();

            var resultNode = response.GetNode("result");
            var balances = resultNode.GetNode("balances");

            foreach (var entry in balances.Children)
            {
                var assetID = entry.GetString("asset");
                var amount = entry.GetDecimal("value");

                var symbol = SymbolFromAssetID(assetID);

                result[symbol] = amount;
            }

            return result;
        }

        public override byte[] GetStorage(string scriptHash, byte[] key)
        {
            var response = QueryRPC("getstorage", new object[] { key.ByteToHex() });
            var result = response.GetString("result");
            if (string.IsNullOrEmpty(result))
            {
                return null;
            }
            return result.HexToBytes();
        }

        // Note: This current implementation requires NeoScan running at port 4000
        public override Dictionary<string, List<UnspentEntry>> GetUnspent(UInt160 hash)
        {
            var url = this._cronExplorerUrl +"/api/main_net/v1/get_balance/" + hash.ToAddress();
            var json = RequestUtils.GetWebRequest(url);

            var root = LunarLabs.Parser.JSON.JSONReader.ReadFromString(json);
            var unspents = new Dictionary<string, List<UnspentEntry>>();

            root = root["balance"];

            foreach (var child in root.Children)
            {
                var symbol = child.GetString("asset");

                List<UnspentEntry> list = new List<UnspentEntry>();
                unspents[symbol] = list;

                var unspentNode = child.GetNode("unspent");
                foreach (var entry in unspentNode.Children)
                {
                    var txid = entry.GetString("txid");
                    var val = entry.GetDecimal("value");
                    var temp = new UnspentEntry() { hash = new UInt256(LuxUtils.ReverseHex(txid).HexToBytes()), value = val, index = entry.GetUInt32("n") };
                    list.Add(temp);
                }
            }

            return unspents;
        }

        // Note: This current implementation requires explorer.cron.global running at port 4000
        public override List<UnspentEntry> GetClaimable(UInt160 hash, out decimal amount)
        {
            var url = this._cronExplorerUrl + "/api/main_net/v1/get_claimable/" + hash.ToAddress();
            var json = RequestUtils.GetWebRequest(url);

            var root = LunarLabs.Parser.JSON.JSONReader.ReadFromString(json);
            var result = new List<UnspentEntry>();

            amount = root.GetDecimal("unclaimed");

            root = root["claimable"];

            foreach (var child in root.Children)
            {
                var txid = child.GetString("txid");
                var index = child.GetUInt32("n");
                var value = child.GetDecimal("unclaimed");

                result.Add(new UnspentEntry() { hash = new UInt256(LuxUtils.ReverseHex(txid).HexToBytes()), index = index, value = value });
            }

            return result;
        }

        public bool SendRawTransaction(string hexTx)
        {
            var response = QueryRPC("sendrawtransaction", new object[] { hexTx });
            if (response == null)
            {
                throw new Exception("Connection failure");
            }

            try
            {
                var temp = response["result"];

                bool result;

                if (temp.HasNode("succeed"))
                {
                    result = temp.GetBool("succeed");
                }
                else
                {
                    result = temp.AsBool();
                }

                return result;
            }
            catch
            {
                return false;
            }
        }

        protected override bool SendTransaction(Transaction tx)
        {
            var rawTx = tx.Serialize(true);
            var hexTx = rawTx.ByteToHex();

            return SendRawTransaction(hexTx);
        }

        public override InvokeResult InvokeScript(byte[] script)
        {
            var invoke = new InvokeResult();
            invoke.state = VM.VMState.NONE;

            var response = QueryRPC("invokescript", new object[] { script.ByteToHex()});

            if (response != null)
            {
                var root = response["result"];
                if (root != null)
                {
                    var stack = root["stack"];
                    invoke.result = ParseStack(stack);

                    invoke.gasSpent = root.GetDecimal("gas_consumed");
                    var temp = root.GetString("state");

                    if (temp.Contains("FAULT"))
                    {
                        invoke.state = VM.VMState.FAULT;
                    }
                    else
                    if (temp.Contains("HALT"))
                    {
                        invoke.state = VM.VMState.HALT;
                    }
                    else
                    {
                        invoke.state = VM.VMState.NONE;
                    }
                }
            }

            return invoke;
        }

        public override Transaction GetTransaction(UInt256 hash)
        {
            var response = QueryRPC("getrawtransaction", new object[] { hash.ToString() });
            if (response != null && response.HasNode("result"))
            {
                var result = response.GetString("result");
                var bytes = result.HexToBytes();
                return Transaction.Unserialize(bytes);
            }
            else
            {
                return null;
            }
        }

        public override uint GetBlockHeight()
        {
            var response = QueryRPC("getblockcount", new object[] { });
            var blockCount = response.GetUInt32("result");
            return blockCount;
        }

        public override Block GetBlock(uint height)
        {
            var response = QueryRPC("getblock", new object[] { height });
            if (response == null || !response.HasNode("result"))
            {
                return null;
            }

            var result = response.GetString("result");

            var bytes = result.HexToBytes();

            using (var stream = new MemoryStream(bytes))
            {
                using (var reader = new BinaryReader(stream))
                {
                    var block = Block.Unserialize(reader);
                    return block;
                }
            }
        }

        public override Block GetBlock(UInt256 hash)
        {
            var response = QueryRPC("getblock", new object[] { hash.ToString() });
            if (response == null || !response.HasNode("result"))
            {
                return null;
            }

            var result = response.GetString("result");

            var bytes = result.HexToBytes();

            using (var stream = new MemoryStream(bytes))
            {
                using (var reader = new BinaryReader(stream))
                {
                    var block = Block.Unserialize(reader);
                    return block;
                }
            }
        }

    }

    public class LocalRPCNode : CronRPC
    {
        private int port;

        public LocalRPCNode(int port, string cronExplorerURL) : base(cronExplorerURL)
        {
            this.port = port;
        }

        protected override string GetRPCEndpoint()
        {
            return $"http://localhost:{port}";
        }
    }

    public enum CronNodesKind
    {
        CRON_GLOBAL,
  //       COZ,
  //      TRAVALA
    }

    public class RemoteRPCNode : CronRPC
    {
        private int rpcIndex = 0;

        private string[] nodes;

        public RemoteRPCNode(string cronExplorerURL, params string[] nodes) : base(cronExplorerURL)
        {
            this.nodes = nodes;
        }

        public RemoteRPCNode(int port, string cronExplorerURL, CronNodesKind kind) : base(cronExplorerURL)
        {
            switch (kind)
            {
                case CronNodesKind.CRON_GLOBAL:
                    {
                        nodes = new string[1];
                        for (int i = 0; i < nodes.Length; i++)
                        {
                            nodes[i] = $"http://seed{i+1}.cron.global:{port}";
                        }
                        break;
                    }
/*
                case CronNodesKind.COZ:
                    {
                        if (port == 10331)
                        {
                            port = 443;
                        }

                        nodes = new string[5];
                        for (int i = 0; i < nodes.Length; i++)
                        {                            
                            nodes[i] = $"http://seed{i}.cityofzion.io:{port}";
                        }
                        break;
                    }

                case CronNodesKind.TRAVALA:
                    {
                        nodes = new string[5];
                        for (int i = 0; i < nodes.Length; i++)
                        {
                            nodes[i] = $"http://seed{i}.travala.com:{port}";
                        }
                        break;
                    }
  */
            }
        }

        protected override string GetRPCEndpoint()
        {
            rpcIndex++;
            if (rpcIndex >= nodes.Length)
            {
                rpcIndex = 0;
            }

            return nodes[rpcIndex];
        }
    }
}
