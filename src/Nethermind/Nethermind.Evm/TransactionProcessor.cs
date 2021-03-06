/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;

namespace Nethermind.Evm
{
    public class TransactionProcessor : ITransactionProcessor
    {
        private readonly IntrinsicGasCalculator _intrinsicGasCalculator = new IntrinsicGasCalculator();
        private readonly ILogger _logger;
        private readonly IStateProvider _stateProvider;
        private readonly IStorageProvider _storageProvider;
        private readonly ISpecProvider _specProvider;
        private readonly IVirtualMachine _virtualMachine;
        private readonly ITransactionTracer _tracer;

        public TransactionProcessor(ISpecProvider specProvider, IStateProvider stateProvider, IStorageProvider storageProvider, IVirtualMachine virtualMachine, ITransactionTracer tracer, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _tracer = tracer ?? throw new ArgumentNullException(nameof(tracer));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _virtualMachine = virtualMachine ?? throw new ArgumentNullException(nameof(virtualMachine));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _storageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));            
        }

        private TransactionReceipt GetNullReceipt(BlockHeader block, long gasUsed)
        {
            block.GasUsed += gasUsed;
            TransactionReceipt transactionReceipt = new TransactionReceipt();
            transactionReceipt.Logs = LogEntry.EmptyLogs;
            transactionReceipt.Bloom = Bloom.Empty;
            transactionReceipt.GasUsed = block.GasUsed;
            if (!_specProvider.GetSpec(block.Number).IsEip658Enabled)
            {
                transactionReceipt.PostTransactionState = _stateProvider.StateRoot; // TODO: do not call it in Byzantium - no longer needed to calculate root hash
            }

            transactionReceipt.StatusCode = StatusCode.Failure;
            return transactionReceipt;
        }

        public TransactionReceipt Execute(
            Transaction transaction,
            BlockHeader block)
        {
            TransactionTrace trace = null;
            if (_tracer.IsTracingEnabled)
            {
                trace = new TransactionTrace();
            }

            IReleaseSpec spec = _specProvider.GetSpec(block.Number);
            Address recipient = transaction.To;
            UInt256 value = transaction.Value;
            UInt256 gasPrice = transaction.GasPrice;
            long gasLimit = (long)transaction.GasLimit;
            byte[] machineCode = transaction.Init;
            byte[] data = transaction.Data ?? Bytes.Empty;

            Address sender = transaction.SenderAddress;
            if (_logger.IsTrace)
            {
                _logger.Trace($"SPEC: {spec.GetType().Name}");
                _logger.Trace("HASH: " + transaction.Hash);
                _logger.Trace("IS_CONTRACT_CREATION: " + transaction.IsContractCreation);
                _logger.Trace("IS_MESSAGE_CALL: " + transaction.IsMessageCall);
                _logger.Trace("IS_TRANSFER: " + transaction.IsTransfer);
                _logger.Trace("SENDER: " + sender);
                _logger.Trace("TO: " + transaction.To);
                _logger.Trace("GAS LIMIT: " + transaction.GasLimit);
                _logger.Trace("GAS PRICE: " + transaction.GasPrice);
                _logger.Trace("VALUE: " + transaction.Value);
                _logger.Trace("DATA_LENGTH: " + (transaction.Data?.Length ?? 0));
                _logger.Trace("NONCE: " + transaction.Nonce);
            }

            if (sender == null)
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"SENDER_NOT_SPECIFIED");
                }

                return GetNullReceipt(block, 0L);
            }

            long intrinsicGas = _intrinsicGasCalculator.Calculate(transaction, spec);
            if (_logger.IsTrace)
            {
                _logger.Trace("INTRINSIC GAS: " + intrinsicGas);
            }

            if (gasLimit < intrinsicGas)
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"GAS_LIMIT_BELOW_INTRINSIC_GAS {gasLimit} < {intrinsicGas}");
                }

                return GetNullReceipt(block, 0L);
            }

            if (gasLimit > block.GasLimit - block.GasUsed)
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"BLOCK_GAS_LIMIT_EXCEEDED {gasLimit} > {block.GasLimit} - {block.GasUsed}");
                }

                return GetNullReceipt(block, 0L);
            }

            if (!_stateProvider.AccountExists(sender))
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"SENDER_ACCOUNT_DOES_NOT_EXIST {sender}");
                }

                _stateProvider.CreateAccount(sender, 0);
            }

            UInt256 senderBalance = _stateProvider.GetBalance(sender);
            if ((ulong)intrinsicGas * gasPrice + value > senderBalance)
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"INSUFFICIENT_SENDER_BALANCE: ({sender})b = {senderBalance}");
                }

                return GetNullReceipt(block, 0L);
            }

            if (transaction.Nonce != _stateProvider.GetNonce(sender))
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"WRONG_TRANSACTION_NONCE: {transaction.Nonce} (expected {_stateProvider.GetNonce(sender)})");
                }

                return GetNullReceipt(block, 0L);
            }

            _stateProvider.IncrementNonce(sender);
            _stateProvider.SubtractFromBalance(sender, (ulong)gasLimit * gasPrice, spec);
            _stateProvider.Commit(spec);

            long unspentGas = gasLimit - intrinsicGas;
            long spentGas = gasLimit;
            List<LogEntry> logEntries = new List<LogEntry>();

            if (transaction.IsContractCreation)
            {
                Rlp addressBaseRlp = Rlp.Encode(
                    Rlp.Encode(sender),
                    Rlp.Encode(_stateProvider.GetNonce(sender) - 1));
                Keccak addressBaseKeccak = Keccak.Compute(addressBaseRlp);
                recipient = new Address(addressBaseKeccak);
            }

            int snapshot = _stateProvider.TakeSnapshot();
            int storageSnapshot = _storageProvider.TakeSnapshot();
            _stateProvider.SubtractFromBalance(sender, value, spec);
            byte statusCode = StatusCode.Failure;

            HashSet<Address> destroyedAccounts = new HashSet<Address>();
            try
            {
                if (transaction.IsContractCreation)
                {
                    // TODO: review tests around it as it fails on Ropsten 230881 when we throw an exception
                    if (_stateProvider.AccountExists(recipient) && !_stateProvider.IsEmptyAccount(recipient))
                    {
                        // TODO: review
//                        throw new TransactionCollisionException();
                    }
                }

                if (transaction.IsTransfer) // TODO: this is never called and wrong, to be removed
                {
                    _stateProvider.SubtractFromBalance(sender, value, spec);
                    _stateProvider.AddToBalance(recipient, value, spec);
                    statusCode = StatusCode.Success;
                }
                else
                {
                    bool isPrecompile = recipient.IsPrecompiled(spec);

                    ExecutionEnvironment env = new ExecutionEnvironment();
                    env.Value = value;
                    env.TransferValue = value;
                    env.Sender = sender;
                    env.ExecutingAccount = recipient;
                    env.CurrentBlock = block;
                    env.GasPrice = gasPrice;
                    env.InputData = data ?? new byte[0];
                    env.CodeInfo = isPrecompile ? new CodeInfo(recipient) : machineCode == null ? _virtualMachine.GetCachedCodeInfo(recipient) : new CodeInfo(machineCode);
                    env.Originator = sender;

                    ExecutionType executionType = isPrecompile
                        ? ExecutionType.DirectPrecompile
                        : transaction.IsContractCreation
                            ? ExecutionType.DirectCreate
                            : ExecutionType.Transaction;

                    TransactionSubstate substate;
                    byte[] output;
                    using (EvmState state = new EvmState(unspentGas, env, executionType, false))
                    {
                        (output, substate) = _virtualMachine.Run(state, spec, trace);
                        unspentGas = state.GasAvailable;
                    }

                    if (substate.ShouldRevert)
                    {
                        if (_logger.IsTrace)
                        {
                            _logger.Trace("REVERTING");
                        }

                        logEntries.Clear();
                        destroyedAccounts.Clear();
                        _stateProvider.Restore(snapshot);
                        _storageProvider.Restore(storageSnapshot);
                    }
                    else
                    {
                        if (transaction.IsContractCreation)
                        {
                            long codeDepositGasCost = output.Length * GasCostOf.CodeDeposit;
                            if (spec.IsEip170Enabled && output.Length > 0x6000)
                            {
                                codeDepositGasCost = long.MaxValue;
                            }

                            if (unspentGas < codeDepositGasCost && spec.IsEip2Enabled)
                            {
                                throw new OutOfGasException();
                            }

                            if (unspentGas >= codeDepositGasCost)
                            {
                                Keccak codeHash = _stateProvider.UpdateCode(output);
                                _stateProvider.UpdateCodeHash(recipient, codeHash, spec);
                                unspentGas -= codeDepositGasCost;
                            }
                        }

                        logEntries.AddRange(substate.Logs);
                        foreach (Address toBeDestroyed in substate.DestroyList)
                        {
                            destroyedAccounts.Add(toBeDestroyed);
                        }

                        statusCode = StatusCode.Success;
                    }

                    spentGas = Refund(gasLimit, unspentGas, substate, sender, gasPrice, spec);
                }
            }
            catch (Exception ex) when (ex is EvmException || ex is OverflowException) // TODO: OverflowException? still needed? hope not
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"EVM EXCEPTION: {ex.GetType().Name}");
                }

                logEntries.Clear();
                destroyedAccounts.Clear();
                _stateProvider.Restore(snapshot);
                _storageProvider.Restore(storageSnapshot);
            }

            foreach (Address toBeDestroyed in destroyedAccounts)
            {
                if (_logger.IsTrace) _logger.Trace($"DESTROYING: {toBeDestroyed}");
                _stateProvider.DeleteAccount(toBeDestroyed);
            }

            if (_logger.IsTrace) _logger.Trace("GAS SPENT: " + spentGas);

            if (!destroyedAccounts.Contains(block.Beneficiary))
            {
                if (!_stateProvider.AccountExists(block.Beneficiary))
                {
                    _stateProvider.CreateAccount(block.Beneficiary, (ulong)spentGas * gasPrice);
                }
                else
                {
                    _stateProvider.AddToBalance(block.Beneficiary, (ulong)spentGas * gasPrice, spec);
                }
            }

            _storageProvider.Commit(spec);
            _stateProvider.Commit(spec);

            block.GasUsed += spentGas;

            if (_tracer.IsTracingEnabled)
            {
                trace.Gas = spentGas;
                _tracer.SaveTrace(Transaction.CalculateHash(transaction), trace);
            }

            return BuildTransactionReceipt(block, statusCode, logEntries.Any() ? logEntries.ToArray() : LogEntry.EmptyLogs, recipient);
        }

        private long Refund(long gasLimit, long unspentGas, TransactionSubstate substate, Address sender, UInt256 gasPrice, IReleaseSpec spec)
        {
            long spentGas = gasLimit - unspentGas;
            long refund = Math.Min(spentGas / 2L, substate.Refund + substate.DestroyList.Count * RefundOf.Destroy);
            if (substate.ShouldRevert) // TODO: not tested anywhere
            {
                refund = 0;
            }

            if (_logger.IsTrace)
            {
                _logger.Trace("REFUNDING UNUSED GAS OF " + unspentGas + " AND REFUND OF " + refund);
            }

            _stateProvider.AddToBalance(sender, (ulong)(unspentGas + refund) * gasPrice, spec);
            spentGas -= refund;
            return spentGas;
        }

        private TransactionReceipt BuildTransactionReceipt(BlockHeader block, byte statusCode, LogEntry[] logEntries, Address recipient)
        {
            TransactionReceipt transactionReceipt = new TransactionReceipt();
            transactionReceipt.Logs = logEntries;
            transactionReceipt.Bloom = logEntries.Length == 0 ? Bloom.Empty : BuildBloom(logEntries);
            transactionReceipt.GasUsed = block.GasUsed;
            if(!_specProvider.GetSpec(block.Number).IsEip658Enabled)
            {
                transactionReceipt.PostTransactionState = _stateProvider.StateRoot;
            }

            transactionReceipt.StatusCode = statusCode;
            transactionReceipt.Recipient = recipient;
            return transactionReceipt;
        }

        public static Bloom BuildBloom(LogEntry[] logEntries)
        {            
            Bloom bloom = new Bloom();
            for (int entryIndex = 0; entryIndex < logEntries.Length; entryIndex++)
            {
                LogEntry logEntry = logEntries[entryIndex];
                byte[] addressBytes = logEntry.LoggersAddress.Bytes;
                bloom.Set(addressBytes);
                for (int topicIndex = 0; topicIndex < logEntry.Topics.Length; topicIndex++)
                {
                    Keccak topic = logEntry.Topics[topicIndex];
                    bloom.Set(topic.Bytes);
                }
            }

            return bloom;
        }
    }
}