using Neo.Lux.Core;
using Neo.Lux.Cryptography;
using Neo.Lux.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace NeoTps
{
    class Program
    {
        static int nodeIndex = 0;
        static LocalRPCNode[] nodes = new LocalRPCNode[]
               {
                    new LocalRPCNode(30333, "http://127.0.0.1:4000"),
                    new LocalRPCNode(30334, "http://127.0.0.1:4000"),
                    new LocalRPCNode(30335, "http://127.0.0.1:4000"),
                    new LocalRPCNode(30336, "http://127.0.0.1:4000")
               };
        static Dictionary<string, decimal> MoneyInfo = new Dictionary<string, decimal>();
        static Dictionary<string, bool> MoneyChanged = new Dictionary<string, bool>();
        static Dictionary<string, KeyPair> KeyPairs = new Dictionary<string, KeyPair>();

        static LocalRPCNode Node
        {
            get
            {
                return nodes[(nodeIndex++) % 4];
            }
        }

        static decimal QueryMoneyByAddress(string address, string hex)
        {
            var balances = Node.GetAssetBalancesOf(address);
            var money = balances.Count > 0 ? balances["NEO"] : 0;
            if (money > 0)
            {
                if (MoneyInfo.ContainsKey(hex))
                    MoneyInfo[hex] = money;
                else
                {
                    MoneyInfo.Add(hex, money);
                }

                if (MoneyChanged.ContainsKey(hex))
                {
                    MoneyChanged[hex] = false;
                }
            }

            return money;
        }

        public static Transaction SendAsset(NeoAPI neoAPI, KeyPair fromKey, string toAddress, string symbol, decimal amount)
        {
            if (String.Equals(fromKey.address, toAddress, StringComparison.OrdinalIgnoreCase))
            {
                throw new NeoException("Source and dest addresses are the same");
            }

            var toScriptHash = toAddress.GetScriptHashFromAddress();
            var target = new Transaction.Output() { scriptHash = new UInt160(toScriptHash), value = amount };
            var targets = new List<Transaction.Output>() { target };

            List<Transaction.Input> inputs;
            List<Transaction.Output> outputs;

            neoAPI.GenerateInputsOutputs(fromKey, symbol, targets, out inputs, out outputs);

            Transaction tx = new Transaction()
            {
                type = TransactionType.ContractTransaction,
                version = 0,
                script = null,
                gas = -1,
                inputs = inputs.ToArray(),
                outputs = outputs.ToArray()
            };

            tx.Sign(fromKey);

            return tx;
        }

        static void Main(string[] args)
        {
            var startKey = "1dd37fba80fec4e6a6f13fd708d8dcb3b29def768017052f6c930fa1c5d90bbb"; //初始账户私钥Hex
            MoneyInfo.Add(startKey, 0);
            KeyPairs.Add(startKey, new KeyPair(startKey.HexToBytes()));

            QueryMoneyByAddress(KeyPairs[startKey].address, startKey);
            Console.WriteLine($"Base account balance=" + MoneyInfo[startKey]);

            if (MoneyInfo[startKey] < 10000)
            {
                Console.WriteLine($"Base account balance error");
                return;
            }

            int round = 1;

            Dictionary<string, KeyPair> waitMoney = new Dictionary<string, KeyPair>();

            while (true)
            {
                Dictionary<string, Transaction> waitSubmitQueue = new Dictionary<string, Transaction>();
                Dictionary<string, KeyPair> tempNewAddress = new Dictionary<string, KeyPair>();

                foreach (var hex in KeyPairs.Keys.ToList())
                {
                    if (MoneyInfo[hex] - 10 > 20)
                    {
                        try
                        {
                            var newAddr = KeyPair.Generate();
                            var tx = SendAsset(Node, KeyPairs[hex], newAddr.address, "NEO", ((int)MoneyInfo[hex] - 10) / 2);

                            waitSubmitQueue.Add(newAddr.PrivateKey.ToHexString(), tx);
                            tempNewAddress.Add(newAddr.PrivateKey.ToHexString(), newAddr);
                        }
                        catch { }
                    }
                }

                uint height;
                try
                {
                    height = Node.GetBlockHeight();
                }
                catch
                {
                    height = 0;
                }

                Console.WriteLine($"Height={height}, Round={(round++)} Queue Count={waitSubmitQueue.Count}");

                int faildCount = 0;

                foreach (var hex in waitSubmitQueue.Keys.ToList())
                {
                    try
                    {
                        if (Node.SendTransaction(null, waitSubmitQueue[hex]))
                        {
                            waitMoney.Add(hex, tempNewAddress[hex]);

                            if (MoneyChanged.ContainsKey(hex))
                            {
                                MoneyChanged[hex] = true;
                            }
                            else
                            {
                                MoneyChanged.Add(hex, true);
                            }
                        }
                        else
                        {
                            faildCount++;
                        }

                        Thread.Sleep(10);
                    }
                    catch (Exception ex)
                    {
                        faildCount++;
                        //Console.WriteLine(ex.Message);
                    }
                }

                if (waitMoney.Count == 0)
                {
                    Console.WriteLine($"Rpc error, wait");
                    Thread.Sleep(2000);
                    continue;
                }

                try
                {
                    height = Node.GetBlockHeight();
                }
                catch
                {
                    height = 0;
                }

                Console.WriteLine($"Height={height}, Submit Tx Count:" + waitMoney.Count + $" (and faild count:{faildCount})");

                //等待所有转账到账新账户

                while (waitMoney.Count > 0)
                {
                    var hex = waitMoney.Keys.ToList()[0];

                    var money = QueryMoneyByAddress(waitMoney[hex].address, hex);

                    if (money > 0)
                    {
                        if (!MoneyInfo.ContainsKey(hex))
                            MoneyInfo.Add(hex, money);

                        if (!KeyPairs.ContainsKey(hex))
                            KeyPairs.Add(hex, waitMoney[hex]);

                        waitMoney.Remove(hex);
                    }
                    else
                    {
                        Thread.Sleep(200);
                    }
                }

                try
                {
                    height = Node.GetBlockHeight();
                }
                catch
                {
                    height = 0;
                }

                Console.WriteLine($"Height={height}, All Tx Check Done");

                //更新老账户

                foreach (var hex in KeyPairs.Keys.ToList())
                {
                    if (!MoneyChanged.ContainsKey(hex) || MoneyChanged[hex])
                        QueryMoneyByAddress(KeyPairs[hex].address, hex);
                }

                try
                {
                    height = Node.GetBlockHeight();
                }
                catch
                {
                    height = 0;
                }

                Console.WriteLine($"Height={height}, Old account balance updated, waiting next block");

                while (true) //等待下一个区块, neo需要2个确认才能使用余额
                {
                    try
                    {
                        var tmpheight = Node.GetBlockHeight();
                        if (tmpheight > height) break;
                    }
                    catch
                    {

                    }

                    Thread.Sleep(100);
                }
            }
        }
    }
}
