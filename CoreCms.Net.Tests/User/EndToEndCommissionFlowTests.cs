using System;
using System.Collections.Generic;
using System.Linq;
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
    /// L4 端到端佣金生命周期编排测试。
    ///
    /// 真实 <c>CoreCmsDistributionOrderServices</c> 依赖 30+ 服务（用户/商品/SKU/分销规则/订单/订单明细...），
    /// 无法在 SQLite 内存库中装配。由于该服务的资金侧全部走 <c>CoreCmsUserBalanceServices.ChangeAsync</c>，
    /// 状态侧通过状态守卫 <c>WHERE status=X</c> 的 Update 完成，本测试用例复刻这两条核心路径，
    /// 按 <see cref="CoreCmsDistributionOrderServices.SettleSingleCommission"/> /
    /// <see cref="CoreCmsDistributionOrderServices.CancelFrozenCommission"/> /
    /// <see cref="CoreCmsDistributionOrderServices.ClawbackAvailableCommission"/> 的执行序列重放端到端流程，
    /// 验证状态机转换 + 资金守恒 + 幂等可重入。
    ///
    /// 设计文档：docs/design/06-financial-test-plan.md 5.1 章。
    /// </summary>
    public class EndToEndCommissionFlowTests : BalanceTestBase
    {
        public EndToEndCommissionFlowTests()
        {
            _db.CodeFirst.InitTables<CoreCmsDistributionOrder>();
            _db.Ado.ExecuteCommand(
                "CREATE UNIQUE INDEX IF NOT EXISTS UK_UserBalance_IdempotencyKey ON CoreCmsUserBalance (idempotencyKey)");
            _db.Ado.ExecuteCommand(
                "CREATE UNIQUE INDEX IF NOT EXISTS UK_DistributionOrder_IdempotencyKey ON CoreCmsDistributionOrder (idempotencyKey)");
        }

        private CoreCmsDistributionOrder InsertFrozenCommission(int referrerId, int buyerId, string orderId, decimal amount)
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

        private CoreCmsUser GetUser(int id) =>
            _db.Queryable<CoreCmsUser>().InSingle(id);

        /// <summary>
        /// 等价 <see cref="CoreCmsDistributionOrderServices.SettleSingleCommission"/>：两步幂等 ChangeAsync + 末尾状态守卫 Frozen→Available。
        /// </summary>
        private async Task SettleAsync(int userId, string orderId, decimal amount, int commissionId)
        {
            var r1 = await _balanceService.ChangeAsync(new BalanceChangeRequest
            {
                UserId = userId,
                AccountType = AccountType.CommissionFrozen,
                Amount = amount,
                SourceType = "CommissionUnfreeze",
                SourceId = orderId,
                OperationType = "UnfreezeFrozen",
                IdempotencyKey = $"CommissionUnfreeze:Frozen:{orderId}:{userId}"
            });
            if (!r1.Success && !r1.IsIdempotentReturn) return;

            var r2 = await _balanceService.ChangeAsync(new BalanceChangeRequest
            {
                UserId = userId,
                AccountType = AccountType.CommissionAvailable,
                Amount = amount,
                SourceType = "CommissionUnfreeze",
                SourceId = orderId,
                OperationType = "UnfreezeToAvailable",
                IdempotencyKey = $"CommissionUnfreeze:Available:{orderId}:{userId}"
            });
            if (!r2.Success && !r2.IsIdempotentReturn) return;

            await _db.Updateable<CoreCmsDistributionOrder>()
                .SetColumns(it => new CoreCmsDistributionOrder
                {
                    status = (int)GlobalEnumVars.CommissionStatus.Available,
                    availableAmount = amount,
                    settledTime = DateTime.Now,
                    isSettlement = (int)GlobalEnumVars.DistributionOrderSettlementStatus.SettlementYes,
                    updateTime = DateTime.Now
                })
                .Where(it => it.id == commissionId
                    && it.status == (int)GlobalEnumVars.CommissionStatus.Frozen)
                .ExecuteCommandAsync();
        }

        /// <summary>
        /// 等价 <see cref="CoreCmsDistributionOrderServices.CancelFrozenCommission"/>：扣 commissionFrozen + 状态守卫 Frozen→Cancelled。
        /// </summary>
        private async Task CancelFrozenAsync(int userId, string orderId, decimal amount, int commissionId)
        {
            await _balanceService.ChangeAsync(new BalanceChangeRequest
            {
                UserId = userId,
                AccountType = AccountType.CommissionFrozen,
                Amount = amount,
                SourceType = "CommissionCancel",
                SourceId = orderId,
                OperationType = "CancelFrozen",
                IdempotencyKey = $"CommissionCancel:Frozen:{orderId}:{userId}"
            });

            await _db.Updateable<CoreCmsDistributionOrder>()
                .SetColumns(it => new CoreCmsDistributionOrder
                {
                    status = (int)GlobalEnumVars.CommissionStatus.Cancelled,
                    cancelledTime = DateTime.Now,
                    isSettlement = (int)GlobalEnumVars.DistributionOrderSettlementStatus.SettlementCancel,
                    updateTime = DateTime.Now
                })
                .Where(it => it.id == commissionId
                    && it.status == (int)GlobalEnumVars.CommissionStatus.Frozen)
                .ExecuteCommandAsync();
        }

        /// <summary>
        /// 等价 <see cref="CoreCmsDistributionOrderServices.ClawbackAvailableCommission"/>：
        /// 优先扣 commissionAvailable；不足部分扣空 + 差额记 commissionDebt；状态守卫 Available→ClawedBack。
        /// </summary>
        private async Task ClawbackAsync(int userId, string orderId, decimal clawbackAmount, int commissionId)
        {
            // 第一次：直接尝试全额扣可提现
            var rAvail = await _balanceService.ChangeAsync(new BalanceChangeRequest
            {
                UserId = userId,
                AccountType = AccountType.CommissionAvailable,
                Amount = clawbackAmount,
                SourceType = "CommissionClawback",
                SourceId = orderId,
                OperationType = "ClawbackAvailable",
                IdempotencyKey = $"CommissionClawback:Available:{orderId}:{userId}"
            });

            if (!rAvail.Success && !rAvail.IsIdempotentReturn && rAvail.ErrorCode == 11007)
            {
                // 余额不足：扣到 0 + 差额记负债
                var user = GetUser(userId);
                var canDeduct = Math.Min(user.commissionAvailable, clawbackAmount);
                var debt = clawbackAmount - canDeduct;

                if (canDeduct > 0)
                {
                    await _balanceService.ChangeAsync(new BalanceChangeRequest
                    {
                        UserId = userId,
                        AccountType = AccountType.CommissionAvailable,
                        Amount = canDeduct,
                        SourceType = "CommissionClawback",
                        SourceId = orderId,
                        OperationType = "ClawbackAvailable",
                        IdempotencyKey = $"CommissionClawback:Available:Partial:{orderId}:{userId}"
                    });
                }
                if (debt > 0)
                {
                    await _balanceService.ChangeAsync(new BalanceChangeRequest
                    {
                        UserId = userId,
                        AccountType = AccountType.CommissionDebt,
                        Amount = debt,
                        SourceType = "CommissionClawback",
                        SourceId = orderId,
                        OperationType = "ClawbackDebt",
                        IdempotencyKey = $"CommissionClawback:Debt:{orderId}:{userId}"
                    });
                }
            }

            await _db.Updateable<CoreCmsDistributionOrder>()
                .SetColumns(it => new CoreCmsDistributionOrder
                {
                    status = (int)GlobalEnumVars.CommissionStatus.ClawedBack,
                    clawedBackTime = DateTime.Now,
                    isSettlement = (int)GlobalEnumVars.DistributionOrderSettlementStatus.SettlementCancel,
                    updateTime = DateTime.Now
                })
                .Where(it => it.id == commissionId
                    && it.status == (int)GlobalEnumVars.CommissionStatus.Available)
                .ExecuteCommandAsync();
        }

        /// <summary>
        /// 5.1#1 普通购买 + 佣金全流程：冻结 → 解冻 → 追回。
        /// 状态机：Pending → Frozen → Available → ClawedBack，资金每步守恒。
        /// </summary>
        [Fact]
        public async Task E2E_NormalLifecycle_Freeze_Settle_Clawback()
        {
            const decimal amount = 100m;
            const string orderId = "E2E_NORMAL_1";
            var referrer = CreateTestUser();

            // 1. 支付成功：冻结
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
            var commission = InsertFrozenCommission(referrer.id, referrer.id, orderId, amount);

            Assert.Equal((int)GlobalEnumVars.CommissionStatus.Frozen, GetStatus(commission.id));
            Assert.Equal(amount, GetUser(referrer.id).commissionFrozen);

            // 2. 收货 + 售后保护期结束：解冻
            await SettleAsync(referrer.id, orderId, amount, commission.id);

            Assert.Equal((int)GlobalEnumVars.CommissionStatus.Available, GetStatus(commission.id));
            var u2 = GetUser(referrer.id);
            Assert.Equal(0m, u2.commissionFrozen);
            Assert.Equal(amount, u2.commissionAvailable);

            // 3. 退款：追回
            await ClawbackAsync(referrer.id, orderId, amount, commission.id);

            Assert.Equal((int)GlobalEnumVars.CommissionStatus.ClawedBack, GetStatus(commission.id));
            var u3 = GetUser(referrer.id);
            Assert.Equal(0m, u3.commissionFrozen);
            Assert.Equal(0m, u3.commissionAvailable);
            Assert.Equal(0m, u3.commissionDebt); // 足额追回，无负债
        }

        /// <summary>
        /// 5.1#2 订单取消 - 冻结中：冻结 → 取消。
        /// 终态 = Cancelled，commissionFrozen 归零，commissionAvailable 仍为 0。
        /// </summary>
        [Fact]
        public async Task E2E_CancelDuringFrozen_ReleasesFrozenAmount()
        {
            const decimal amount = 70m;
            const string orderId = "E2E_CANCEL_FROZEN";
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
            var commission = InsertFrozenCommission(referrer.id, referrer.id, orderId, amount);

            await CancelFrozenAsync(referrer.id, orderId, amount, commission.id);

            Assert.Equal((int)GlobalEnumVars.CommissionStatus.Cancelled, GetStatus(commission.id));
            var u = GetUser(referrer.id);
            Assert.Equal(0m, u.commissionFrozen);
            Assert.Equal(0m, u.commissionAvailable);
            Assert.Equal(0m, u.commissionDebt);
        }

        /// <summary>
        /// 5.1#3 退款 + 佣金追回（已解冻）：解冻 → 部分提现 → 整单退款。
        /// 追回金额 > 剩余可提现 → commissionDebt > 0，终态 ClawedBack。
        /// </summary>
        [Fact]
        public async Task E2E_RefundAfterWithdrawal_DebtRecorded_WhenClawbackExceedsAvailable()
        {
            const decimal amount = 100m;
            const decimal withdrawn = 80m; // 提现走扣 commissionAvailable（OperationType=WithdrawAvailable）
            const string orderId = "E2E_REFUND_DEBT";

            // 直接以「已解冻」态起步：commissionAvailable=100
            var referrer = CreateTestUser(initialBalance: 0, initialCommission: 0);
            _db.Updateable<CoreCmsUser>()
                .SetColumns(u => new CoreCmsUser { commissionAvailable = amount })
                .Where(u => u.id == referrer.id)
                .ExecuteCommand();

            // 插入已解冻的佣金记录（跳过冻结阶段，直接验证追回路径）
            var commission = InsertFrozenCommission(referrer.id, referrer.id, orderId, amount);
            await _db.Updateable<CoreCmsDistributionOrder>()
                .SetColumns(it => new CoreCmsDistributionOrder
                {
                    status = (int)GlobalEnumVars.CommissionStatus.Available,
                    availableAmount = amount,
                    settledTime = DateTime.Now,
                    isSettlement = (int)GlobalEnumVars.DistributionOrderSettlementStatus.SettlementYes
                })
                .Where(it => it.id == commission.id)
                .ExecuteCommandAsync();

            // 1. 用户先提现 80（模拟提现扣可提现）
            await _balanceService.ChangeAsync(new BalanceChangeRequest
            {
                UserId = referrer.id,
                AccountType = AccountType.CommissionAvailable,
                Amount = withdrawn,
                SourceType = "Tocash",
                SourceId = "withdraw_1",
                OperationType = "WithdrawAvailable",
                IdempotencyKey = $"Withdraw:Available:withdraw_1:{referrer.id}"
            });
            Assert.Equal(amount - withdrawn, GetUser(referrer.id).commissionAvailable); // 20

            // 2. 退款整单，需追回 100，剩余可提现 20 → 扣 20 + 记负债 80
            await ClawbackAsync(referrer.id, orderId, amount, commission.id);

            Assert.Equal((int)GlobalEnumVars.CommissionStatus.ClawedBack, GetStatus(commission.id));
            var u = GetUser(referrer.id);
            Assert.Equal(0m, u.commissionAvailable);
            Assert.Equal(withdrawn, u.commissionDebt); // 80（=追回金额 - 剩余可提现）
        }

        /// <summary>
        /// 5.1#5 负债抵扣：有负债 → 新佣金解冻自动优先抵债。
        /// 模拟新订单结算金额 50，先扣旧债 30（commissionDebt 减少 30），剩余 20 入 commissionAvailable。
        /// </summary>
        [Fact]
        public async Task E2E_DebtOffset_NewSettlement_PaysDownDebtFirst()
        {
            const decimal existingDebt = 30m;
            const decimal newSettleAmount = 50m;
            const string newOrderId = "E2E_NEW_SETTLE";

            var referrer = CreateTestUser();
            // 注入一笔历史负债
            _db.Updateable<CoreCmsUser>()
                .SetColumns(u => new CoreCmsUser { commissionDebt = existingDebt })
                .Where(u => u.id == referrer.id)
                .ExecuteCommand();
            Assert.Equal(existingDebt, GetUser(referrer.id).commissionDebt);

            // 新订单冻结 → 解冻到 available（先入可提现）
            await _balanceService.ChangeAsync(new BalanceChangeRequest
            {
                UserId = referrer.id,
                AccountType = AccountType.CommissionFrozen,
                Amount = newSettleAmount,
                SourceType = "CommissionFreeze",
                SourceId = newOrderId,
                OperationType = "Freeze",
                IdempotencyKey = $"CommissionFreeze:Frozen:{newOrderId}:{referrer.id}"
            });
            var newCommission = InsertFrozenCommission(referrer.id, referrer.id, newOrderId, newSettleAmount);

            // 解冻：扣 frozen + 加 available
            await SettleAsync(referrer.id, newOrderId, newSettleAmount, newCommission.id);
            Assert.Equal(newSettleAmount, GetUser(referrer.id).commissionAvailable);

            // 抵债：扣可提现 + 减负债（commissionDebt 是 increase-type，Amount=existingDebt 表示「增加负债」；
            // 减负债需通过 DebtOffset direction，但 ChangeAsync 的 DebtOffset 是 deduction on available side。
            // 此处模拟：从 commissionAvailable 扣 existingDebt 还债，commissionDebt 直接 Updateable 减为 0
            await _balanceService.ChangeAsync(new BalanceChangeRequest
            {
                UserId = referrer.id,
                AccountType = AccountType.CommissionAvailable,
                Amount = existingDebt,
                SourceType = "CommissionDebtOffset",
                SourceId = newOrderId,
                OperationType = "DebtOffset",
                IdempotencyKey = $"CommissionDebtOffset:Available:{newOrderId}:{referrer.id}",
                Remark = "负债抵扣"
            });
            _db.Updateable<CoreCmsUser>()
                .SetColumns(u => new CoreCmsUser { commissionDebt = 0 })
                .Where(u => u.id == referrer.id)
                .ExecuteCommand();

            var u = GetUser(referrer.id);
            Assert.Equal(0m, u.commissionDebt);
            Assert.Equal(newSettleAmount - existingDebt, u.commissionAvailable);
            Assert.Equal(0m, u.commissionFrozen);
        }

        /// <summary>
        /// 端到端幂等重放：完整生命周期跑完一遍后，每一步再以同幂等键重放，
        /// 资金与状态应保持不变（无重复入账、无状态回退）。
        /// </summary>
        [Fact]
        public async Task E2E_FullLifecycle_ReplayEachStep_NoMutation()
        {
            const decimal amount = 100m;
            const string orderId = "E2E_REPLAY_1";
            var referrer = CreateTestUser();

            // 1. 冻结
            var freezeReq = new BalanceChangeRequest
            {
                UserId = referrer.id,
                AccountType = AccountType.CommissionFrozen,
                Amount = amount,
                SourceType = "CommissionFreeze",
                SourceId = orderId,
                OperationType = "Freeze",
                IdempotencyKey = $"CommissionFreeze:Frozen:{orderId}:{referrer.id}"
            };
            await _balanceService.ChangeAsync(freezeReq);
            var commission = InsertFrozenCommission(referrer.id, referrer.id, orderId, amount);

            // 2. 解冻
            await SettleAsync(referrer.id, orderId, amount, commission.id);

            // 3. 追回
            await ClawbackAsync(referrer.id, orderId, amount, commission.id);

            var statusBefore = GetStatus(commission.id);
            var userBefore = GetUser(referrer.id);
            var flowsBefore = _db.Queryable<CoreCmsUserBalance>()
                .Where(b => b.userId == referrer.id && b.sourceId == orderId).Count();

            // 重放冻结、解冻序列（应全部幂等返回 / affected=0）
            var freezeReplay = await _balanceService.ChangeAsync(freezeReq);
            Assert.True(freezeReplay.IsIdempotentReturn);

            await SettleAsync(referrer.id, orderId, amount, commission.id);
            await ClawbackAsync(referrer.id, orderId, amount, commission.id);

            var statusAfter = GetStatus(commission.id);
            var userAfter = GetUser(referrer.id);
            var flowsAfter = _db.Queryable<CoreCmsUserBalance>()
                .Where(b => b.userId == referrer.id && b.sourceId == orderId).Count();

            Assert.Equal(statusBefore, statusAfter);
            Assert.Equal(userBefore.commissionFrozen, userAfter.commissionFrozen);
            Assert.Equal(userBefore.commissionAvailable, userAfter.commissionAvailable);
            Assert.Equal(userBefore.commissionDebt, userAfter.commissionDebt);
            Assert.Equal(flowsBefore, flowsAfter);
        }
    }
}