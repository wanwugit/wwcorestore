/***********************************************************************
 *            Project: CoreCms
 *        ProjectName: 核心内容管理系统                                
 *                Web: https://www.corecms.net                      
 *             Author: 大灰灰                                          
 *              Email: jianweie@163.com                                
 *         CreateTime: 2021/1/31 21:45:10
 *        Description: 暂无
 ***********************************************************************/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using CoreCms.Net.Configuration;
using CoreCms.Net.IRepository;
using CoreCms.Net.IRepository.UnitOfWork;
using CoreCms.Net.IServices;
using CoreCms.Net.Loging;
using CoreCms.Net.Model.Entities;
using CoreCms.Net.Model.ViewModels.Basics;
using CoreCms.Net.Model.ViewModels.UI;
using CoreCms.Net.Model.ViewModels.DTO;
using CoreCms.Net.Model.ViewModels.Financial;
using SqlSugar;


namespace CoreCms.Net.Services
{
    /// <summary>
    /// 分销商订单记录表 接口实现
    /// </summary>
    public class CoreCmsDistributionOrderServices : BaseServices<CoreCmsDistributionOrder>, ICoreCmsDistributionOrderServices
    {
        private readonly ICoreCmsDistributionOrderRepository _dal;
        private readonly ICoreCmsUserServices _userServices;
        private readonly ICoreCmsDistributionServices _distributionServices;
        private readonly ICoreCmsOrderServices _orderServices;
        private readonly ICoreCmsOrderItemServices _orderItemServices;
        private readonly ICoreCmsProductsDistributionServices _productsDistributionServices;
        private readonly ICoreCmsProductsServices _productsServices;
        private readonly ICoreCmsUserBalanceServices _balanceServices;
        private readonly ICoreCmsGoodsServices _goodsServices;

        private readonly IUnitOfWork _unitOfWork;
        public CoreCmsDistributionOrderServices(IUnitOfWork unitOfWork, ICoreCmsDistributionOrderRepository dal, ICoreCmsDistributionServices distributionServices, ICoreCmsUserBalanceServices balanceServices, ICoreCmsOrderServices orderServices, ICoreCmsUserServices userServices, ICoreCmsOrderItemServices orderItemServices, ICoreCmsProductsDistributionServices productsDistributionServices, ICoreCmsProductsServices productsServices, ICoreCmsGoodsServices goodsServices)
        {
            this._dal = dal;
            _distributionServices = distributionServices;
            _balanceServices = balanceServices;
            _orderServices = orderServices;
            _userServices = userServices;
            _orderItemServices = orderItemServices;
            _productsDistributionServices = productsDistributionServices;
            _productsServices = productsServices;
            _goodsServices = goodsServices;
            base.BaseDal = dal;
            _unitOfWork = unitOfWork;
        }

        /// <summary>
        /// 佣金结算/退款追佣门禁。默认读 Distribution:CommissionSettleEnabled（默认 "0"=禁用）。
        /// 测试子类可 override 返回 true 以绕过 appsettings 依赖。
        /// </summary>
        protected virtual bool CommissionSettleEnabled()
        {
            var v = AppSettingsHelper.GetContent("Distribution", "CommissionSettleEnabled");
            var s = v?.Trim();
            return string.Equals(s, "1", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
        }

        #region 实现重写增删改查操作==========================================================

        /// <summary>
        /// 重写异步插入方法
        /// </summary>
        /// <param name="entity">实体数据</param>
        /// <returns></returns>
        public new async Task<AdminUiCallBack> InsertAsync(CoreCmsDistributionOrder entity)
        {
            return await _dal.InsertAsync(entity);
        }

        /// <summary>
        /// 重写异步更新方法方法
        /// </summary>
        /// <param name="entity"></param>
        /// <returns></returns>
        public new async Task<AdminUiCallBack> UpdateAsync(CoreCmsDistributionOrder entity)
        {
            return await _dal.UpdateAsync(entity);
        }

        /// <summary>
        /// 重写异步更新方法方法
        /// </summary>
        /// <param name="entity"></param>
        /// <returns></returns>
        public new async Task<AdminUiCallBack> UpdateAsync(List<CoreCmsDistributionOrder> entity)
        {
            return await _dal.UpdateAsync(entity);
        }

        /// <summary>
        /// 重写删除指定ID的数据
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public new async Task<AdminUiCallBack> DeleteByIdAsync(object id)
        {
            return await _dal.DeleteByIdAsync(id);
        }

        /// <summary>
        /// 重写删除指定ID集合的数据(批量删除)
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public new async Task<AdminUiCallBack> DeleteByIdsAsync(int[] ids)
        {
            return await _dal.DeleteByIdsAsync(ids);
        }

        #endregion

        #region 重写根据条件查询分页数据
        /// <summary>
        ///     重写根据条件查询分页数据
        /// </summary>
        /// <param name="predicate">判断集合</param>
        /// <param name="orderByType">排序方式</param>
        /// <param name="pageIndex">当前页面索引</param>
        /// <param name="pageSize">分布大小</param>
        /// <param name="orderByExpression"></param>
        /// <param name="blUseNoLock">是否使用WITH(NOLOCK)</param>
        /// <returns></returns>
        public new async Task<IPageList<CoreCmsDistributionOrder>> QueryPageAsync(Expression<Func<CoreCmsDistributionOrder, bool>> predicate,
            Expression<Func<CoreCmsDistributionOrder, object>> orderByExpression, OrderByType orderByType, int pageIndex = 1,
            int pageSize = 20, bool blUseNoLock = false)
        {
            return await _dal.QueryPageAsync(predicate, orderByExpression, orderByType, pageIndex, pageSize, blUseNoLock);
        }
        #endregion


        #region 添加分销订单关联记录
        /// <summary>
        /// 添加分销订单关联记录（状态机版：单级冻结 + 幂等）
        /// </summary>
        /// <param name="order"></param>
        /// <returns></returns>
        public async Task<WebApiCallBack> AddData(CoreCmsOrder order)
        {
            var jm = new WebApiCallBack();

            if (!CommissionSettleEnabled())
            {
                jm.status = true;
                jm.msg = "降级模式：分销佣金结算已禁用";
                return jm;
            }

            var user = await _userServices.QueryByClauseAsync(p => p.id == order.userId);
            if (user is not { parentId: > 0 })
            {
                jm.status = true;
                return jm;
            }
            // 自购不返佣
            if (user.parentId == user.id)
            {
                jm.status = true;
                return jm;
            }

            var referrerId = user.parentId;
            var idempotencyKey = $"CommissionFreeze:{order.orderId}:{referrerId}";

            // 幂等返回：已存在冻结记录则跳过
            var existing = await _dal.QueryByClauseAsync(p => p.idempotencyKey == idempotencyKey && !p.isDelete);
            if (existing != null)
            {
                jm.status = true;
                jm.msg = "已冻结";
                return jm;
            }

            // 计算一级佣金
            var orderItems = await _orderItemServices.QueryListByClauseAsync(p => p.orderId == order.orderId);
            var goodIds = orderItems.Select(p => p.goodsId).ToList();
            var productIds = orderItems.Select(p => p.productId).ToList();
            var goods = await _goodsServices.QueryListByClauseAsync(p => goodIds.Contains(p.id));
            var products = await _productsServices.QueryListByClauseAsync(p => productIds.Contains(p.id));
            var productsDistributions = await _productsDistributionServices.QueryListByClauseAsync(p => productIds.Contains(p.productsId));

            var commission = await _distributionServices.GetGradeAndCommission(referrerId);
            var dto = commission.data as DistributionDto;
            if (!(commission.status && dto != null))
            {
                // 不是分销商的，不返利
                jm.status = true;
                return jm;
            }

            decimal amount = 0;
            foreach (var item in orderItems)
            {
                var good = goods.Find(p => p.id == item.goodsId);
                if (good == null) continue;
                var product = products.Find(p => p.id == item.productId);
                if (product == null) continue;
                var itemAmount = item.amount - item.promotionAmount;
                if (itemAmount < 0) itemAmount = 0;

                if (good.productsDistributionType == (int)GlobalEnumVars.ProductsDistributionType.Global)
                {
                    if (dto.commission_1 == null) continue;
                    if (dto.commission_1.type == (int)GlobalEnumVars.DistributionCommissiontype.COMMISSION_TYPE_FIXED)
                        amount += dto.commission_1.discount;
                    else
                        amount += Math.Round(dto.commission_1.discount * itemAmount / 100, 2);
                }
                else if (good.productsDistributionType == (int)GlobalEnumVars.ProductsDistributionType.Detail)
                {
                    var productsDistribution = productsDistributions.Find(p => p.productsId == item.productId);
                    if (productsDistribution == null) continue;
                    if (productsDistribution.levelOne > 0)
                        amount += Math.Round(productsDistribution.levelOne * item.nums, 2);
                }
            }

            if (amount <= 0)
            {
                jm.status = true;
                return jm;
            }

            // 创建佣金记录（Frozen 状态）
            var iData = new CoreCmsDistributionOrder
            {
                userId = referrerId,
                buyUserId = order.userId,
                orderId = order.orderId,
                amount = amount,
                frozenAmount = amount,
                availableAmount = 0,
                level = 1,
                isSettlement = (int)GlobalEnumVars.DistributionOrderSettlementStatus.SettlementNo,
                status = (int)GlobalEnumVars.CommissionStatus.Frozen,
                frozenTime = DateTime.Now,
                isDelete = false,
                createTime = DateTime.Now,
            };
            await _dal.InsertAsync(iData);

            // 冻结用户佣金余额（幂等 ChangeAsync）
            var freezeReq = new BalanceChangeRequest
            {
                UserId = referrerId,
                AccountType = AccountType.CommissionFrozen,
                Amount = amount,
                SourceType = "CommissionFreeze",
                SourceId = order.orderId,
                OperationType = "Freeze",
                IdempotencyKey = $"CommissionFreeze:Frozen:{order.orderId}:{referrerId}",
                Remark = "佣金冻结"
            };
            await _balanceServices.ChangeAsync(freezeReq);

            jm.status = true;
            return jm;
        }

        #endregion

        #region 订单结算处理事件
        /// <summary>
        /// 订单结算处理事件（状态机版：Frozen → Available，双账户原子，幂等）
        /// </summary>
        /// <param name="orderId"></param>
        /// <returns></returns>
        public async Task<WebApiCallBack> FinishOrder(string orderId)
        {
            var jm = new WebApiCallBack();

            if (!CommissionSettleEnabled())
            {
                jm.status = true;
                jm.msg = "降级模式：分销佣金结算已禁用";
                return jm;
            }

            var order = await _orderServices.QueryByClauseAsync(p => p.orderId == orderId && p.status == (int)GlobalEnumVars.OrderStatus.Complete);
            if (order == null)
            {
                jm.msg = "订单查询失败";
                return jm;
            }

            var list = await _dal.QueryListByClauseAsync(
                p => p.orderId == orderId
                  && p.status == (int)GlobalEnumVars.CommissionStatus.Frozen
                  && !p.isDelete);

            if (list == null || !list.Any())
            {
                jm.status = true;
                jm.msg = "无待结算佣金";
                return jm;
            }

            foreach (var item in list)
            {
                await SettleSingleCommission(item);
            }

            jm.status = true;
            return jm;
        }

        /// <summary>
        /// 结算单笔佣金：两步幂等 ChangeAsync + 末尾状态守卫 Frozen→Available
        /// 中间崩溃可重入：步骤1/2幂等键兜底；步骤3状态守卫 affected=0 视为已处理。
        /// </summary>
        private async Task SettleSingleCommission(CoreCmsDistributionOrder commission)
        {
            var orderId = commission.orderId;
            var userId = commission.userId;
            var amount = commission.frozenAmount;

            // 1. 扣 commissionFrozen（幂等）
            var reqFrom = new BalanceChangeRequest
            {
                UserId = userId,
                AccountType = AccountType.CommissionFrozen,
                Amount = amount,
                SourceType = "CommissionUnfreeze",
                SourceId = orderId,
                OperationType = "UnfreezeFrozen",
                IdempotencyKey = $"CommissionUnfreeze:Frozen:{orderId}:{userId}",
                Remark = "佣金解冻-扣冻结"
            };
            var r1 = await _balanceServices.ChangeAsync(reqFrom);
            if (!r1.Success && !r1.IsIdempotentReturn)
            {
                NLogUtil.WriteFileLog(NLog.LogLevel.Error, LogType.Other,
                    "佣金结算", $"解冻扣冻结失败: {reqFrom.IdempotencyKey} -> {r1.ErrorCode} {r1.ErrorMessage}", null);
                return;
            }

            // 2. 加 commissionAvailable（幂等）
            var reqTo = new BalanceChangeRequest
            {
                UserId = userId,
                AccountType = AccountType.CommissionAvailable,
                Amount = amount,
                SourceType = "CommissionUnfreeze",
                SourceId = orderId,
                OperationType = "UnfreezeToAvailable",
                IdempotencyKey = $"CommissionUnfreeze:Available:{orderId}:{userId}",
                Remark = "佣金解冻-加可提现"
            };
            var r2 = await _balanceServices.ChangeAsync(reqTo);
            if (!r2.Success && !r2.IsIdempotentReturn)
            {
                NLogUtil.WriteFileLog(NLog.LogLevel.Error, LogType.Other,
                    "佣金结算", $"解冻加可提现失败: {reqTo.IdempotencyKey} -> {r2.ErrorCode} {r2.ErrorMessage}", null);
                return;
            }

            // 3. 状态守卫更新：Frozen → Available
            await _dal.UpdateAsync(
                p => new CoreCmsDistributionOrder
                {
                    status = (int)GlobalEnumVars.CommissionStatus.Available,
                    availableAmount = amount,
                    settledTime = DateTime.Now,
                    isSettlement = (int)GlobalEnumVars.DistributionOrderSettlementStatus.SettlementYes,
                    updateTime = DateTime.Now
                },
                p => p.id == commission.id
                  && p.status == (int)GlobalEnumVars.CommissionStatus.Frozen);
        }
        #endregion

        #region 作废订单
        /// <summary>
        /// 作废订单（退款追佣，状态机版）
        /// 对该订单的佣金记录按状态分别处理：
        ///   Frozen     → Cancelled     扣 commissionFrozen
        ///   Available  → ClawedBack    扣 commissionAvailable，不足部分记 commissionDebt
        /// 各步骤幂等，未结算(isSettlement)同步更新为 SettlementCancel
        /// </summary>
        /// <param name="orderId">订单编号</param>
        /// <returns></returns>
        public async Task<WebApiCallBack> CancleOrderByOrderId(string orderId)
        {
            var jm = new WebApiCallBack();

            if (!CommissionSettleEnabled())
            {
                jm.status = true;
                jm.msg = "降级模式：分销佣金退款追佣已禁用";
                return jm;
            }

            var list = await _dal.QueryListByClauseAsync(
                p => p.orderId == orderId
                  && (p.status == (int)GlobalEnumVars.CommissionStatus.Frozen
                      || p.status == (int)GlobalEnumVars.CommissionStatus.Available)
                  && !p.isDelete);

            if (list == null || !list.Any())
            {
                jm.msg = "无可作废的佣金记录";
                return jm;
            }

            foreach (var item in list)
            {
                if (item.status == (int)GlobalEnumVars.CommissionStatus.Frozen)
                {
                    await CancelFrozenCommission(item);
                }
                else if (item.status == (int)GlobalEnumVars.CommissionStatus.Available)
                {
                    await ClawbackAvailableCommission(item);
                }
            }

            jm.status = true;
            jm.msg = "操作成功";
            return jm;
        }

        /// <summary>
        /// 取消冻结中佣金：扣 commissionFrozen + 状态守卫 Frozen → Cancelled
        /// </summary>
        private async Task CancelFrozenCommission(CoreCmsDistributionOrder commission)
        {
            var orderId = commission.orderId;
            var userId = commission.userId;
            var amount = commission.frozenAmount;

            // 1. 扣 commissionFrozen（幂等）
            var req = new BalanceChangeRequest
            {
                UserId = userId,
                AccountType = AccountType.CommissionFrozen,
                Amount = amount,
                SourceType = "CommissionCancel",
                SourceId = orderId,
                OperationType = "CancelFrozen",
                IdempotencyKey = $"CommissionCancel:Frozen:{orderId}:{userId}",
                Remark = "佣金取消-扣冻结"
            };
            var r = await _balanceServices.ChangeAsync(req);
            if (!r.Success && !r.IsIdempotentReturn)
            {
                NLogUtil.WriteFileLog(NLog.LogLevel.Error, LogType.Other,
                    "佣金取消", $"取消冻结佣金失败: {req.IdempotencyKey} -> {r.ErrorCode} {r.ErrorMessage}", null);
                return;
            }

            // 2. 状态守卫 Frozen → Cancelled
            await _dal.UpdateAsync(
                p => new CoreCmsDistributionOrder
                {
                    status = (int)GlobalEnumVars.CommissionStatus.Cancelled,
                    cancelledTime = DateTime.Now,
                    isSettlement = (int)GlobalEnumVars.DistributionOrderSettlementStatus.SettlementCancel,
                    updateTime = DateTime.Now
                },
                p => p.id == commission.id
                  && p.status == (int)GlobalEnumVars.CommissionStatus.Frozen);
        }

        /// <summary>
        /// 追回已解冻佣金：扣 commissionAvailable，不足部分记 commissionDebt + 状态守卫 Available → ClawedBack
        /// </summary>
        private async Task ClawbackAvailableCommission(CoreCmsDistributionOrder commission)
        {
            var orderId = commission.orderId;
            var userId = commission.userId;
            var clawbackAmount = commission.availableAmount;
            if (clawbackAmount <= 0) return;

            // 1. 扣 commissionAvailable（幂等，可能因余额不足失败）
            var reqAvail = new BalanceChangeRequest
            {
                UserId = userId,
                AccountType = AccountType.CommissionAvailable,
                Amount = clawbackAmount,
                SourceType = "CommissionClawback",
                SourceId = orderId,
                OperationType = "ClawbackAvailable",
                IdempotencyKey = $"CommissionClawback:Available:{orderId}:{userId}",
                Remark = "佣金追回-扣可提现"
            };
            var rAvail = await _balanceServices.ChangeAsync(reqAvail);

            // 余额不足：扣可提现到 0，差额记负债
            if (!rAvail.Success && !rAvail.IsIdempotentReturn && rAvail.ErrorCode == 11007)
            {
                // 1.a 先查当前可提现以确定差额
                var user = await _userServices.QueryByClauseAsync(p => p.id == userId);
                if (user != null)
                {
                    var canDeduct = Math.Min(user.commissionAvailable, clawbackAmount);
                    var debt = clawbackAmount - canDeduct;

                    if (canDeduct > 0)
                    {
                        var reqPartial = new BalanceChangeRequest
                        {
                            UserId = userId,
                            AccountType = AccountType.CommissionAvailable,
                            Amount = canDeduct,
                            SourceType = "CommissionClawback",
                            SourceId = orderId,
                            OperationType = "ClawbackAvailable",
                            IdempotencyKey = $"CommissionClawback:Available:Partial:{orderId}:{userId}",
                            Remark = "佣金追回-扣可提现(部分)"
                        };
                        await _balanceServices.ChangeAsync(reqPartial);
                    }
                    if (debt > 0)
                    {
                        var reqDebt = new BalanceChangeRequest
                        {
                            UserId = userId,
                            AccountType = AccountType.CommissionDebt,
                            Amount = debt,
                            SourceType = "CommissionClawback",
                            SourceId = orderId,
                            OperationType = "ClawbackDebt",
                            IdempotencyKey = $"CommissionClawback:Debt:{orderId}:{userId}",
                            Remark = "佣金追回-记负债"
                        };
                        await _balanceServices.ChangeAsync(reqDebt);
                    }
                }
            }

            // 2. 状态守卫 Available → ClawedBack
            await _dal.UpdateAsync(
                p => new CoreCmsDistributionOrder
                {
                    status = (int)GlobalEnumVars.CommissionStatus.ClawedBack,
                    clawedBackTime = DateTime.Now,
                    isSettlement = (int)GlobalEnumVars.DistributionOrderSettlementStatus.SettlementCancel,
                    updateTime = DateTime.Now
                },
                p => p.id == commission.id
                  && p.status == (int)GlobalEnumVars.CommissionStatus.Available);
        }
        #endregion


        #region 获取下级推广订单数量
        /// <summary>
        ///     获取下级推广订单数量
        /// </summary>
        /// <param name="parentId">父类序列</param>
        /// <param name="type">1获取1级，其他为2级,0为全部</param>
        /// <param name="thisMonth">显示当月</param>
        /// <returns></returns>
        public async Task<int> QueryChildOrderCountAsync(int parentId, int type = 1, bool thisMonth = false)
        {
            return await _dal.QueryChildOrderCountAsync(parentId, type, thisMonth);

        }
        #endregion


        #region 获取下级推广订单金额
        /// <summary>
        ///     获取下级推广订单金额
        /// </summary>
        /// <param name="parentId">父类序列</param>
        /// <param name="type">1获取1级，其他为2级,0为全部</param>
        /// <param name="thisMonth">显示当月</param>
        /// <returns></returns>
        public async Task<decimal> QueryChildOrderMoneySumAsync(int parentId, int type = 1, bool thisMonth = false)
        {

            return await _dal.QueryChildOrderMoneySumAsync(parentId, type, thisMonth);

        }
        #endregion
    }
}
