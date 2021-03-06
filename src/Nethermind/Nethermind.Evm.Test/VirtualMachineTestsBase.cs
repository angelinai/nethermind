﻿using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;
using NUnit.Framework;
using Org.BouncyCastle.Utilities.Encoders;

namespace Nethermind.Evm.Test
{
    public class VirtualMachineTestsBase : ITransactionTracer
    {
        private readonly ConsoleTransactionTracer _tracer = new ConsoleTransactionTracer(new UnforgivingJsonSerializer());
        private readonly IEthereumSigner _ethereumSigner;
        private readonly ITransactionProcessor _processor;
        private readonly ISnapshotableDb _stateDb;
        private readonly IDbProvider _storageDbProvider;
        protected internal readonly ISpecProvider SpecProvider;
        protected internal IStateProvider TestState { get; }
        protected internal IStorageProvider Storage { get; }

        protected internal static Address Sender { get; } = TestObject.AddressA;
        protected internal static Address Recipient { get; } = TestObject.AddressB;

        protected virtual UInt256 BlockNumber => 10000;

        protected IReleaseSpec Spec => SpecProvider.GetSpec(BlockNumber);

        public VirtualMachineTestsBase()
        {
            SpecProvider = RopstenSpecProvider.Instance;
            ILogManager logger = NullLogManager.Instance;
            IDb codeDb = new MemDb();
            _stateDb = new SnapshotableDb(new MemDb());
            StateTree stateTree = new StateTree(_stateDb);
            TestState = new StateProvider(stateTree, codeDb, logger);
            _storageDbProvider = new MemDbProvider(logger);
            Storage = new StorageProvider(_storageDbProvider, TestState, logger);
            _ethereumSigner = new EthereumSigner(SpecProvider, logger);
            IBlockhashProvider blockhashProvider = new TestBlockhashProvider();
            IVirtualMachine virtualMachine = new VirtualMachine(TestState, Storage, blockhashProvider, logger);

            _processor = new TransactionProcessor(SpecProvider, TestState, Storage, virtualMachine, this, logger);
        }

        protected TransactionTrace TransactionTrace { get; private set; }

        public bool IsTracingEnabled
        {
            get => _tracer.IsTracingEnabled;
            protected set => _tracer.IsTracingEnabled = value;
        }

        public void SaveTrace(Keccak hash, TransactionTrace trace)
        {
            TransactionTrace = trace;
            _tracer.SaveTrace(hash, trace);
        }

        [SetUp]
        public void Setup()
        {
            _tracer.IsTracingEnabled = false;
            TransactionTrace = null;

            _stateDbSnapshot = _stateDb.TakeSnapshot();
            _storageDbSnapshot = _storageDbProvider.TakeSnapshot();
            _stateRoot = TestState.StateRoot;
        }

        private int _stateDbSnapshot;
        private int _storageDbSnapshot;
        private Keccak _stateRoot;

        [TearDown]
        public void TearDown()
        {
            Storage.ClearCaches();
            TestState.Reset();
            TestState.StateRoot = _stateRoot;

            _storageDbProvider.Restore(_storageDbSnapshot);
            _stateDb.Restore(_stateDbSnapshot);
        }

        protected TransactionReceipt Execute(params byte[] code)
        {
            return Execute(BlockNumber, 100000, code);
        }

        protected TransactionReceipt Execute(UInt256 blockNumber, long gasLimit, byte[] code)
        {
            TestState.CreateAccount(Sender, 100.Ether());
            TestState.CreateAccount(Recipient, 100.Ether());
            Keccak codeHash = TestState.UpdateCode(code);
            TestState.UpdateCodeHash(TestObject.AddressB, codeHash, SpecProvider.GenesisSpec);

            TestState.Commit(SpecProvider.GenesisSpec);

            Transaction transaction = Build.A.Transaction
                .WithGasLimit((ulong) gasLimit)
                .WithGasPrice(1)
                .WithTo(TestObject.AddressB)
                .SignedAndResolved(_ethereumSigner, TestObject.PrivateKeyA, blockNumber)
                .TestObject;

            Block block = Build.A.Block.WithNumber(blockNumber).TestObject;
            TransactionReceipt receipt = _processor.Execute(transaction, block.Header);
            return receipt;
        }

        protected void AssertGas(TransactionReceipt receipt, long gas)
        {
            Assert.AreEqual(gas, receipt.GasUsed, "gas");
        }

        protected void AssertStorage(UInt256 address, Keccak value)
        {
            Assert.AreEqual(value.Bytes, Storage.Get(new StorageAddress(Recipient, address)).PadLeft(32), "storage");
        }

        protected void AssertStorage(UInt256 address, byte[] value)
        {
            Assert.AreEqual(value.PadLeft(32), Storage.Get(new StorageAddress(Recipient, address)).PadLeft(32), "storage");
        }

        protected void AssertStorage(UInt256 address, BigInteger value)
        {
            Assert.AreEqual(value.ToBigEndianByteArray(), Storage.Get(new StorageAddress(Recipient, address)), "storage");
        }
        
        protected void AssertCodeHash(Address address, Keccak codeHash)
        {
            Assert.AreEqual(codeHash, TestState.GetCodeHash(address), "code hash");
        }

        protected class Prepare
        {
            private readonly List<byte> _byteCode = new List<byte>();
            public static Prepare EvmCode => new Prepare();
            public byte[] Done => _byteCode.ToArray();

            public Prepare Op(Instruction instruction)
            {
                _byteCode.Add((byte) instruction);
                return this;
            }

            public Prepare Create(byte[] code, BigInteger value)
            {
                StoreDataInMemory(0, code);
                PushData(code.Length);
                PushData(0);
                PushData(value);
                Op(Instruction.CREATE);
                return this;
            }
            
            public Prepare Create2(byte[] code, byte[] salt, BigInteger value)
            {
                StoreDataInMemory(0, code);
                PushData(salt);
                PushData(code.Length);
                PushData(0);
                PushData(value);
                Op(Instruction.CREATE2);
                return this;
            }
            
            public Prepare ForInitOf(byte[] codeToBeDeployed)
            {
                if (codeToBeDeployed.Length > 32)
                {
                    throw new NotSupportedException();
                }
                
                PushData(codeToBeDeployed.PadRight(32));
                PushData(0);
                Op(Instruction.MSTORE);
                PushData(codeToBeDeployed.Length);
                PushData(0);
                Op(Instruction.RETURN);
                
                return this;
            }

            public Prepare Call(Address address, long gasLimit)
            {
                PushData(0);
                PushData(0);
                PushData(0);
                PushData(0);
                PushData(0);
                PushData(address);
                PushData(gasLimit);
                Op(Instruction.CALL);
                return this;
            }

            public Prepare PushData(Address address)
            {
                PushData(address.Bytes);
                return this;
            }

            public Prepare PushData(BigInteger data)
            {
                PushData(data.ToBigEndianByteArray());
                return this;
            }

            public Prepare PushData(string data)
            {
                PushData(Bytes.FromHexString(data));
                return this;
            }

            public Prepare PushData(byte[] data)
            {
                _byteCode.Add((byte) (Instruction.PUSH1 + (byte) data.Length - 1));
                _byteCode.AddRange(data);
                return this;
            }

            public Prepare PushData(byte data)
            {
                PushData(new[] {data});
                return this;
            }

            public Prepare Data(string data)
            {
                _byteCode.AddRange(Bytes.FromHexString(data));
                return this;
            }

            public Prepare Data(byte[] data)
            {
                _byteCode.AddRange(data);
                return this;
            }

            public Prepare Data(byte data)
            {
                _byteCode.Add(data);
                return this;
            }
            
            public Prepare StoreDataInMemory(int poisition, byte[] data)
            {
                if (poisition % 32 != 0)
                {
                    throw new NotSupportedException();
                }
                
                for (int i = 0; i < data.Length; i += 32)
                {
                    PushData(data.Slice(i, data.Length - i).PadRight(32));
                    PushData(i);
                    Op(Instruction.MSTORE);    
                }
                
                return this;
            }
        }
    }
}