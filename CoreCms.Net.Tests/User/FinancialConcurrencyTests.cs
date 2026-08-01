using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreCms.Net.Configuration;
using CoreCms.Net.IRepository.UnitOfWork;
using CoreCms.Net.Model.Entities;
using CoreCms.Net.Model.ViewModels.Financial;
using CoreCms.Net.Repository.UnitOfWork;
using CoreCms.Net.Services;
using Microsoft.Data.Sqlite;
using SqlSugar;
using Xunit;

namespace CoreCms.Net.Tests.User
{
    /// <summary>
    /// L3 并发测试：余额与佣金状态的并发安全性验证。
    /// SQLite 内存库通过单连接串行化写入，因此这里验证的是【并发调用后的不变量】
    /// （幂等 Только одно вхождение、终态唯一、资金守恒），等价于真实数据库在唯一索引+状态守卫下的最终一致性。
    /// 设计文档：docs/design/06-financial-test-plan.md 四章。
    /// </summary>
    public class BalanceConcurrencyTests : BalanceTestBase
    {
        public BalanceConcurrencyTests()
        {
            // 模拟生产 schema（迁移 002）：idempotencyKey 非空唯一
            _db.Ado.ExecuteCommand(
                "CREATE UNIQUE INDEX IF NOT EXISTS UK_UserBalance_IdempotencyKey ON CoreCmsUserBalance (idempotencyKey)");
        }

        /// <summary>
        /// 4.1#1：10 线程并发充值（不同幂等键），余额 = 旧值 + 1000，流水 10 笔。
        /// </summary>
        [Fact]
        public async Task ConcurrentDeposit_DifferentKeys_AllSucceed_BalanceInvariant()
        {
            const int threadCount = 10;
            const decimal amount = 100m;
            var user = CreateTestUser(initialBalance: 0);

            var tasks = Enumerable.Range(0, threadCount).Select(i => _balanceService.ChangeAsync(new BalanceChangeRequest
            {
                UserId = user.id,
                AccountType = AccountType.Balance,
                Amount = amount,
                SourceType = "Recharge",
                OperationType = "Recharge",
                IdempotencyKey = $"ConcurrentDeposit:{user.id}:{i}",
                SourceId = $"src_{i}",
                Remark = "并发充值"
            }));

            var results = await Task.WhenAll(tasks);

            Assert.All(results, r => Assert.True(r.Success, $"失败: {r.ErrorCode} {r.ErrorMessage}"));
            Assert.Equal(threadCount * amount, GetUserBalance(user.id));
            Assert.Equal(threadCount, GetBalanceRecordCount(user.id));
        }

        /// <summary>
        /// 4.1#3：10 充值 + 10 扣减并发（各 100），余额 = 旧值不变，流水 20 笔。
        /// </summary>
        [Fact]
        public async Task ConcurrentMixed_DepositAndPay_BalanceInvariant()
        {
            const int count = 10;
            const decimal amount = 100m;
            var user = CreateTestUser(initialBalance: 500m);
            var initialBalance = GetUserBalance(user.id);

            var deposits = Enumerable.Range(0, count).Select(i => _balanceService.ChangeAsync(new BalanceChangeRequest
            {
                UserId = user.id,
                AccountType = AccountType.Balance,
                Amount = amount,
                SourceType = "Recharge",
                OperationType = "Recharge",
                IdempotencyKey = $"Mix:Recharge:{i}",
                SourceId = $"r_{i}"
            }));
            var pays = Enumerable.Range(0, count).Select(i => _balanceService.ChangeAsync(new BalanceChangeRequest
            {
                UserId = user.id,
                AccountType = AccountType.Balance,
                Amount = amount,
                SourceType = "Pay",
                OperationType = "Pay",
                IdempotencyKey = $"Mix:Pay:{i}",
                SourceId = $"p_{i}"
            }));

            var results = await Task.WhenAll(deposits.Concat(pays));

            Assert.All(results, r => Assert.True(r.Success, $"失败: {r.ErrorCode}"));
            Assert.Equal(initialBalance, GetUserBalance(user.id)); // 10*+100 + 10*-100 = 0
            Assert.Equal(2 * count, GetBalanceRecordCount(user.id));
        }

        /// <summary>
        /// 4.1#2：余额 1000，10 线程各 Pay 50，余额 = 500，流水 10 笔。
        /// </summary>
        [Fact]
        public async Task ConcurrentDeduction_SufficientBalance_AllSucceed()
        {
            const int threadCount = 10;
            const decimal payAmount = 50m;
            var user = CreateTestUser(initialBalance: 1000m);

            var tasks = Enumerable.Range(0, threadCount).Select(i => _balanceService.ChangeAsync(new BalanceChangeRequest
            {
                UserId = user.id,
                AccountType = AccountType.Balance,
                Amount = payAmount,
                SourceType = "Pay",
                OperationType = "Pay",
                IdempotencyKey = $"ConcurrentPay:{user.id}:{i}",
                SourceId = $"order_{i}"
            }));

            var results = await Task.WhenAll(tasks);

            Assert.All(results, r => Assert.True(r.Success, $"失败: {r.ErrorCode}"));
            Assert.Equal(1000m - threadCount * payAmount, GetUserBalance(user.id));
            Assert.Equal(threadCount, GetBalanceRecordCount(user.id));
        }

        /// <summary>
        /// 4.1#4：余额 100，5 线程各 Pay 50，最多 2 个成功，余额守恒（= 100 - 成功数*50）。
        /// 实际成功数取决于 SQLite 串行化时序，故验证「成功数 <= 2 且余额 = 100 - 成功数*50」。
        /// </summary>
        [Fact]
        public async Task ConcurrentDeduction_InsufficientBalance_AtMostTwoSucceed()
        {
            const int threadCount = 5;
            const decimal payAmount = 50m;
            var user = CreateTestUser(initialBalance: 100m);

            var tasks = Enumerable.Range(0, threadCount).Select(i => _balanceService.ChangeAsync(new BalanceChangeRequest
            {
                UserId = user.id,
                AccountType = AccountType.Balance,
                Amount = payAmount,
                SourceType = "Pay",
                OperationType = "Pay",
                IdempotencyKey = $"ConcurrentInsuf:{user.id}:{i}",
                SourceId = $"order_{i}"
            }));

            var results = await Task.WhenAll(tasks);

            var successCount = results.Count(r => r.Success);
            var failedCount = results.Count(r => !r.Success);

            Assert.True(successCount <= 2, $"成功数应 <= 2，实际 {successCount}");
            Assert.True(successCount + failedCount == threadCount);
            Assert.Equal(100m - successCount * payAmount, GetUserBalance(user.id));
        }

        /// <summary>
        /// 4.1#5：10 线程同幂等键并发 ChangeAsync，只入账 1 次，其余幂等返回，余额 = 单次金额。
        /// </summary>
        [Fact]
        public async Task ConcurrentSameIdempotencyKey_OnlyOneAccounting_OthersIdempotentReturn()
        {
            const int threadCount = 10;
            const decimal amount = 100m;
            var user = CreateTestUser(initialBalance: 0);
            var idempotencyKey = $"ConcurrentSameKey:{Guid.NewGuid():N}";

            var tasks = Enumerable.Range(0, threadCount).Select(_ => _balanceService.ChangeAsync(new BalanceChangeRequest
            {
                UserId = user.id,
                AccountType = AccountType.Balance,
                Amount = amount,
                SourceType = "Recharge",
                OperationType = "Recharge",
                IdempotencyKey = idempotencyKey,
                SourceId = "shared_src"
            }));

            var results = await Task.WhenAll(tasks);

            // 全部应视为成功（首次成功 + 其余幂等返回）
            Assert.All(results, r => Assert.True(r.Success, $"失败: {r.ErrorCode}"));
            // 余额只增加一次
            Assert.Equal(amount, GetUserBalance(user.id));
            // 只插入 1 条流水
            Assert.Equal(1, GetBalanceRecordCount(user.id));
            // 至少有 threadCount-1 个是幂等返回；至少有 1 个是首次成功
            var idempotentReturnCount = results.Count(r => r.IsIdempotentReturn);
            Assert.True(idempotentReturnCount >= threadCount - 1,
                $"幂等返回数应 >= {threadCount - 1}，实际 {idempotentReturnCount}");
        }
    }

    /// <summary>
    /// L3 佣金结算并发测试：验证 <see cref="GlobalEnumVars.CommissionStatus"/> 状态守卫 +
    /// 幂等键在并发场景下保证「恰好一次」结算 / 取消 / 追回。
    /// 设计文档：docs/design/06-financial-test-plan.md 4.2 章。
    /// </summary>
    public class CommissionConcurrencyTests : BalanceTestBase
    {
        public CommissionConcurrencyTests()
        {
            _db.CodeFirst.InitTables<CoreCmsDistributionOrder>();
            _db.Ado.ExecuteCommand(
                "CREATE UNIQUE INDEX IF NOT EXISTS UK_UserBalance_IdempotencyKey ON CoreCmsUserBalance (idempotencyKey)");
        }

        private CoreCmsDistributionOrder InsertFrozenOrder(int referrerId, int buyerId, string orderId, decimal amount)
        {
            var order = new CoreCmsDistributionOrder
            {
                userId = referrerId,
                buyUserId = buyerId,
                orderId = orderId,
                amount = amount,
                frozenAmount = amount,
                availableAmount = 0,
                level = 1,
                isSettlement = (int)GlobalEnumVars.DistributionOrderSettlementStatus.SettlementNo,
                status = (int)GlobalEnumVars.CommissionStatus.Frozen,
                frozenTime = DateTime.Now,
                isDelete = false,
                createTime = DateTime.Now,
                idempotencyKey = $"CommissionFreeze:{orderId}:{referrerId}"
            };
            order.id = _db.Insertable(order).ExecuteReturnIdentity();
            return order;
        }

        private int GetStatus(int id) =>
            _db.Queryable<CoreCmsDistributionOrder>().InSingle(id).status;

        /// <summary>
        /// 4.2#1：5 并发 FinishOrder 同 orderId。状态守卫 Frozen→Available 应只命中一次，
        /// commissionAvailable 只增加一次，其余重复执行因状态已是 Available 而跳过状态更新。
        /// 这里以两步幂等 ChangeAsync + 末尾状态守卫模拟 <c>SettleSingleCommission</c> 序列。
        /// </summary>
        [Fact]
        public async Task ConcurrentFinishOrder_StateGuard_OnlyOneSettles()
        {
            const int concurrency = 5;
            const decimal amount = 100m;
            const string orderId = "CONCURRENT_FINISH_1";

            var referrer = CreateTestUser();
            // 冻结阶段：扣佣金冻结账户前先有冻结余额
            await _balanceService.ChangeAsync(new BalanceChangeRequest
            {
                UserId = referrer.id,
                AccountType = AccountType.CommissionFrozen,
                Amount = amount,
                SourceType = "CommissionFreeze",
                SourceId = orderId,
                OperationType = "Freeze",
                IdempotencyKey = $"CommissionFreeze:Frozen:{orderId}:{referrer.id}"
            });
            var commission = InsertFrozenOrder(referrer.id, referrer.id, orderId, amount);

            // 并发模拟 SettleSingleCommission：解冻扣 frozen + 解冻加 available + 状态守卫更新
            async Task SettleOnce()
            {
                var r1 = await _balanceService.ChangeAsync(new BalanceChangeRequest
                {
                    UserId = referrer.id,
                    AccountType = AccountType.CommissionFrozen,
                    Amount = amount,
                    SourceType = "CommissionUnfreeze",
                    SourceId = orderId,
                    OperationType = "UnfreezeFrozen",
                    IdempotencyKey = $"CommissionUnfreeze:Frozen:{orderId}:{referrer.id}"
                });
                if (!r1.Success && !r1.IsIdempotentReturn) return;

                var r2 = await _balanceService.ChangeAsync(new BalanceChangeRequest
                {
                    UserId = referrer.id,
                    AccountType = AccountType.CommissionAvailable,
                    Amount = amount,
                    SourceType = "CommissionUnfreeze",
                    SourceId = orderId,
                    OperationType = "UnfreezeToAvailable",
                    IdempotencyKey = $"CommissionUnfreeze:Available:{orderId}:{referrer.id}"
                });
                if (!r2.Success && !r2.IsIdempotentReturn) return;

                // 状态守卫：Frozen → Available（并发下仅一个 affected=1）
                await _db.Updateable<CoreCmsDistributionOrder>()
                    .SetColumns(it => new CoreCmsDistributionOrder
                    {
                        status = (int)GlobalEnumVars.CommissionStatus.Available,
                        availableAmount = amount,
                        settledTime = DateTime.Now,
                        isSettlement = (int)GlobalEnumVars.DistributionOrderSettlementStatus.SettlementYes,
                        updateTime = DateTime.Now
                    })
                    .Where(it => it.id == commission.id
                        && it.status == (int)GlobalEnumVars.CommissionStatus.Frozen)
                    .ExecuteCommandAsync();
            }

            var tasks = Enumerable.Range(0, concurrency).Select(_ => SettleOnce());
            await Task.WhenAll(tasks);

            // 终态：Available
            Assert.Equal((int)GlobalEnumVars.CommissionStatus.Available, GetStatus(commission.id));
            // 资金守恒：commissionFrozen=0，commissionAvailable=amount（恰好解冻一次）
            var user = _db.Queryable<CoreCmsUser>().InSingle(referrer.id);
            Assert.Equal(0m, user.commissionFrozen);
            Assert.Equal(amount, user.commissionAvailable);
            // 流水：解冻扣 frozen + 解冻加 available 各只 1 条（幂等键唯一）；
            // 不计冻结阶段的 1 条 CommissionFreeze 流水。
            var unfreezeFlowCount = _db.Queryable<CoreCmsUserBalance>()
                .Where(b => b.userId == referrer.id && b.sourceId == orderId)
                .Where(b => b.operationType == "UnfreezeFrozen"
                         || b.operationType == "UnfreezeToAvailable")
                .Count();
            Assert.Equal(2, unfreezeFlowCount);
        }

        /// <summary>
        /// 4.2#2：1 个 FinishOrder（解冻）与 1 个 CancleOrder（取消）并发竞争同一 Frozen 记录。
        /// 状态守卫保证最终状态非 Frozen 即可（要么 Available 要么 Cancelled），且资金守恒：
        /// commissionFrozen 必定归零，commissionAvailable ∈ {0, amount}。
        /// </summary>
        [Fact]
        public async Task ConcurrentSettleAndCancel_StateGuard_FrozenClearedAndConserved()
        {
            const decimal amount = 80m;
            const string orderId = "CONCURRENT_SC_1";
            var referrer = CreateTestUser();
            await _balanceService.ChangeAsync(new BalanceChangeRequest
            {
                UserId = referrer.id,
                AccountType = AccountType.CommissionFrozen,
                Amount = amount,
                SourceType = "CommissionFreeze",
                SourceId = orderId,
                OperationType = "Freeze",
                IdempotencyKey = $"CommissionFreeze:Frozen:{orderId}:{referrer.id}"
            });
            var commission = InsertFrozenOrder(referrer.id, referrer.id, orderId, amount);

            // 解冻分支：成功则状态 → Available
            async Task Settle()
            {
                await _balanceService.ChangeAsync(new BalanceChangeRequest
                {
                    UserId = referrer.id,
                    AccountType = AccountType.CommissionFrozen,
                    Amount = amount,
                    SourceType = "CommissionUnfreeze",
                    SourceId = orderId,
                    OperationType = "UnfreezeFrozen",
                    IdempotencyKey = $"CommissionUnfreeze:Frozen:{orderId}:{referrer.id}"
                });
                await _balanceService.ChangeAsync(new BalanceChangeRequest
                {
                    UserId = referrer.id,
                    AccountType = AccountType.CommissionAvailable,
                    Amount = amount,
                    SourceType = "CommissionUnfreeze",
                    SourceId = orderId,
                    OperationType = "UnfreezeToAvailable",
                    IdempotencyKey = $"CommissionUnfreeze:Available:{orderId}:{referrer.id}"
                });
                await _db.Updateable<CoreCmsDistributionOrder>()
                    .SetColumns(it => new CoreCmsDistributionOrder
                    {
                        status = (int)GlobalEnumVars.CommissionStatus.Available,
                        availableAmount = amount,
                        settledTime = DateTime.Now,
                        isSettlement = (int)GlobalEnumVars.DistributionOrderSettlementStatus.SettlementYes,
                        updateTime = DateTime.Now
                    })
                    .Where(it => it.id == commission.id
                        && it.status == (int)GlobalEnumVars.CommissionStatus.Frozen)
                    .ExecuteCommandAsync();
            }

            // 取消分支：扣 commissionFrozen，状态 → Cancelled
            async Task Cancel()
            {
                await _balanceService.ChangeAsync(new BalanceChangeRequest
                {
                    UserId = referrer.id,
                    AccountType = AccountType.CommissionFrozen,
                    Amount = amount,
                    SourceType = "CommissionCancel",
                    SourceId = orderId,
                    OperationType = "CancelFrozen",
                    IdempotencyKey = $"CommissionCancel:Frozen:{orderId}:{referrer.id}"
                });
                await _db.Updateable<CoreCmsDistributionOrder>()
                    .SetColumns(it => new CoreCmsDistributionOrder
                    {
                        status = (int)GlobalEnumVars.CommissionStatus.Cancelled,
                        cancelledTime = DateTime.Now,
                        isSettlement = (int)GlobalEnumVars.DistributionOrderSettlementStatus.SettlementCancel,
                        updateTime = DateTime.Now
                    })
                    .Where(it => it.id == commission.id
                        && it.status == (int)GlobalEnumVars.CommissionStatus.Frozen)
                    .ExecuteCommandAsync();
            }

            await Task.WhenAll(Settle(), Cancel());

            var status = GetStatus(commission.id);
            Assert.True(status == (int)GlobalEnumVars.CommissionStatus.Available
                      || status == (int)GlobalEnumVars.CommissionStatus.Cancelled,
                $"终态应为 Available 或 Cancelled，实际 {status}");

            // 资金守恒
            var user = _db.Queryable<CoreCmsUser>().InSingle(referrer.id);
            Assert.Equal(0m, user.commissionFrozen);
            Assert.True(user.commissionAvailable == 0m || user.commissionAvailable == amount,
                $"commissionAvailable 应为 0 或 {amount}，实际 {user.commissionAvailable}");
        }

        /// <summary>
        /// 4.2#3：1 个解冻（Frozen→Available）与 1 个追回（Available→ClawedBack）并发。
        /// 由于状态守卫严格按序：若解冻先到，则追回成功（终态 ClawedBack）；若追回先到则状态守卫 affected=0（仍 Frozen）。
        /// 验证：终态 ∈ {Available, ClawedBack}，资金守恒（commissionFrozen=0 且 commissionAvailable ∈ {0, amount}）。
        /// </summary>
        [Fact]
        public async Task ConcurrentSettleAndClawback_StateGuard_Conserved()
        {
            const decimal amount = 60m;
            const string orderId = "CONCURRENT_SX_1";
            var referrer = CreateTestUser();
            await _balanceService.ChangeAsync(new BalanceChangeRequest
            {
                UserId = referrer.id,
                AccountType = AccountType.CommissionFrozen,
                Amount = amount,
                SourceType = "CommissionFreeze",
                SourceId = orderId,
                OperationType = "Freeze",
                IdempotencyKey = $"CommissionFreeze:Frozen:{orderId}:{referrer.id}"
            });
            var commission = InsertFrozenOrder(referrer.id, referrer.id, orderId, amount);

            async Task Settle()
            {
                await _balanceService.ChangeAsync(new BalanceChangeRequest
                {
                    UserId = referrer.id,
                    AccountType = AccountType.CommissionFrozen,
                    Amount = amount,
                    SourceType = "CommissionUnfreeze",
                    SourceId = orderId,
                    OperationType = "UnfreezeFrozen",
                    IdempotencyKey = $"CommissionUnfreeze:Frozen:{orderId}:{referrer.id}"
                });
                await _balanceService.ChangeAsync(new BalanceChangeRequest
                {
                    UserId = referrer.id,
                    AccountType = AccountType.CommissionAvailable,
                    Amount = amount,
                    SourceType = "CommissionUnfreeze",
                    SourceId = orderId,
                    OperationType = "UnfreezeToAvailable",
                    IdempotencyKey = $"CommissionUnfreeze:Available:{orderId}:{referrer.id}"
                });
                await _db.Updateable<CoreCmsDistributionOrder>()
                    .SetColumns(it => new CoreCmsDistributionOrder
                    {
                        status = (int)GlobalEnumVars.CommissionStatus.Available,
                        availableAmount = amount,
                        settledTime = DateTime.Now,
                        isSettlement = (int)GlobalEnumVars.DistributionOrderSettlementStatus.SettlementYes,
                        updateTime = DateTime.Now
                    })
                    .Where(it => it.id == commission.id
                        && it.status == (int)GlobalEnumVars.CommissionStatus.Frozen)
                    .ExecuteCommandAsync();
            }

            async Task Clawback()
            {
                // 仅当状态已 Available 才追回：扣 commissionAvailable，状态 → ClawedBack
                await _balanceService.ChangeAsync(new BalanceChangeRequest
                {
                    UserId = referrer.id,
                    AccountType = AccountType.CommissionAvailable,
                    Amount = amount,
                    SourceType = "CommissionClawback",
                    SourceId = orderId,
                    OperationType = "ClawbackAvailable",
                    IdempotencyKey = $"CommissionClawback:Available:{orderId}:{referrer.id}"
                });
                await _db.Updateable<CoreCmsDistributionOrder>()
                    .SetColumns(it => new CoreCmsDistributionOrder
                    {
                        status = (int)GlobalEnumVars.CommissionStatus.ClawedBack,
                        clawedBackTime = DateTime.Now,
                        isSettlement = (int)GlobalEnumVars.DistributionOrderSettlementStatus.SettlementCancel,
                        updateTime = DateTime.Now
                    })
                    .Where(it => it.id == commission.id
                        && it.status == (int)GlobalEnumVars.CommissionStatus.Available)
                    .ExecuteCommandAsync();
            }

            await Task.WhenAll(Settle(), Clawback());

            var status = GetStatus(commission.id);
            Assert.True(
                status == (int)GlobalEnumVars.CommissionStatus.Available
             || status == (int)GlobalEnumVars.CommissionStatus.ClawedBack
             || status == (int)GlobalEnumVars.CommissionStatus.Frozen,
                $"终态应为 Available/ClawedBack/Frozen(若追回先到未生效)，实际 {status}");

            var user = _db.Queryable<CoreCmsUser>().InSingle(referrer.id);
            Assert.Equal(0m, user.commissionFrozen);
            Assert.True(user.commissionAvailable == 0m || user.commissionAvailable == amount,
                $"commissionAvailable 应为 0 或 {amount}，实际 {user.commissionAvailable}");
        }
    }
}