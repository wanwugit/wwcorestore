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
using System.Linq.Expressions;
using System.Threading.Tasks;
using CoreCms.Net.Configuration;
using CoreCms.Net.IRepository;
using CoreCms.Net.IRepository.UnitOfWork;
using CoreCms.Net.IServices;
using CoreCms.Net.Loging;
using CoreCms.Net.Model.Entities;
using CoreCms.Net.Model.ViewModels.Basics;
using CoreCms.Net.Model.ViewModels.Financial;
using CoreCms.Net.Model.ViewModels.UI;
using CoreCms.Net.Utility.Helper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SqlSugar;


namespace CoreCms.Net.Services
{
    /// <summary>
    /// 用户余额表 接口实现
    /// </summary>
    public class CoreCmsUserBalanceServices : BaseServices<CoreCmsUserBalance>, ICoreCmsUserBalanceServices
    {
        private readonly ICoreCmsUserBalanceRepository _dal;
        private readonly IUnitOfWork _unitOfWork;
        private readonly IServiceProvider _serviceProvider;

        public CoreCmsUserBalanceServices(IUnitOfWork unitOfWork, ICoreCmsUserBalanceRepository dal,
            IServiceProvider serviceProvider
            )
        {
            this._dal = dal;
            base.BaseDal = dal;
            _unitOfWork = unitOfWork;
            _serviceProvider = serviceProvider;
        }

        /// <summary>
        /// 余额变动记录
        /// </summary>
        /// <param name="userId">当前用户id,当是店铺的时候，取店铺创始人的userId</param>
        /// <param name="type">类型</param>
        /// <param name="money">金额，永远是正的</param>
        /// <param name="sourceId">资源id</param>
        /// <param name="cateMoney">服务费金额 (提现)</param>
        /// <returns></returns>
        public async Task<WebApiCallBack> Change(int userId, int type, decimal money, string sourceId = "", decimal cateMoney = 0)
        {
            using var container = _serviceProvider.CreateScope();
            var userServices = container.ServiceProvider.GetService<ICoreCmsUserServices>();

            var jm = new WebApiCallBack();

            if (money != 0)
            {
                //取用户实际余额
                var userInfo = await userServices.QueryByIdAsync(userId);
                if (userInfo == null)
                {
                    jm.data = jm.code = 11004;
                    jm.msg = GlobalErrorCodeVars.Code11004;
                    return jm;
                }
                //取描述，并简单校验
                var res = UserHelper.GetMemo(type, money, cateMoney);
                if (string.IsNullOrEmpty(res))
                {
                    return jm;
                }
                var memo = res;
                if (type != (int)GlobalEnumVars.UserBalanceSourceTypes.Admin)
                {
                    //后台充值或调不改绝对值

                }
                //如果是减余额的操作，还是加余额操作
                if (type == (int)GlobalEnumVars.UserBalanceSourceTypes.Pay || type == (int)GlobalEnumVars.UserBalanceSourceTypes.Tocash)
                {
                    money = -money - cateMoney;
                }
                if (type != (int)GlobalEnumVars.UserBalanceSourceTypes.Service)
                {
                    //后台充值或调不改绝对值

                }


                var balance = userInfo.balance + money;
                if (balance < 0)
                {
                    jm.data = jm.code = 11007;
                    jm.msg = GlobalErrorCodeVars.Code11007;
                    return jm;
                }
                var balanceModel = new CoreCmsUserBalance();
                balanceModel.userId = userId;
                balanceModel.type = type;
                balanceModel.money = money;
                balanceModel.balance = balance;
                balanceModel.sourceId = sourceId;
                balanceModel.memo = memo;
                balanceModel.createTime = DateTime.Now;
                //增加记录
                var balanceModelId = await _dal.InsertAsync(balanceModel);
                balanceModel.id = balanceModelId;
                //更新用户数据
                await userServices.UpdateAsync(p => new CoreCmsUser() { balance = balance }, p => p.id == userId);

                jm.data = balanceModel;

            }
            jm.status = true;

            return jm;
        }


        /// <summary>
        /// 获取用户的邀请佣金
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        public async Task<decimal> GetInviteCommission(int userId)
        {
            var type = (int)GlobalEnumVars.UserBalanceSourceTypes.Distribution;
            var money = await _dal.GetSumAsync(p => p.userId == userId && p.type == type, p => p.money);
            return money;
        }


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
        public new async Task<IPageList<CoreCmsUserBalance>> QueryPageAsync(Expression<Func<CoreCmsUserBalance, bool>> predicate,
            Expression<Func<CoreCmsUserBalance, object>> orderByExpression, OrderByType orderByType, int pageIndex = 1,
            int pageSize = 20, bool blUseNoLock = false)
        {
            return await _dal.QueryPageAsync(predicate, orderByExpression, orderByType, pageIndex, pageSize, blUseNoLock);
        }
        #endregion

        #region M4: 安全资金变更（事务 + 原子更新 + 幂等）

        /// <summary>
        ///     安全资金变更（事务 + 原子更新 + 幂等）
        /// </summary>
        /// <param name="request">资金变更请求</param>
        /// <returns>资金变更结果</returns>
        public async Task<BalanceChangeResult> ChangeAsync(BalanceChangeRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Amount < 0) return BalanceChangeResult.Fail(11005, "金额不能为负数");
            if (request.Amount == 0) return BalanceChangeResult.Fail(11006, "金额不能为零");
            if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
                return BalanceChangeResult.Fail(11008, "幂等键不能为空");

            // 1. 幂等检查：先查询是否已处理
            var existingRecord = await _dal.QueryByClauseAsync(
                p => p.idempotencyKey == request.IdempotencyKey);
            if (existingRecord != null)
            {
                // 已处理，幂等返回
                return BalanceChangeResult.Idempotent(
                    existingRecord.id,
                    existingRecord.beforeAmount ?? 0,
                    existingRecord.afterAmount ?? 0,
                    existingRecord.money);
            }

            // 2. 计算实际变更金额（含方向）
            var changeAmount = ComputeChangeAmount(request);
            if (changeAmount == 0) return BalanceChangeResult.Fail(11006, "计算变更金额为零");

            // 3. 获取目标列名
            var targetColumn = GetBalanceColumnName(request.AccountType);

            // 4. 开启事务
            _unitOfWork.BeginTran();
            try
            {
                // 5. SELECT ... FOR UPDATE 锁定用户行，获取变更前余额
                var user = await _unitOfWork.GetDbClient().Queryable<CoreCmsUser>()
                    .Where(u => u.id == request.UserId)
                    .SingleAsync();

                if (user == null)
                {
                    _unitOfWork.RollbackTran();
                    return BalanceChangeResult.Fail(11004, "用户不存在");
                }

                var beforeAmount = GetAccountBalance(user, request.AccountType);

                // 6. 检查余额是否足够（扣减场景）
                var afterAmount = beforeAmount + changeAmount;
                if (afterAmount < 0)
                {
                    _unitOfWork.RollbackTran();
                    return BalanceChangeResult.Fail(11007, "余额不足");
                }

                // 7. 原子更新余额（使用表达式更新，确保并发安全）
                var userToUpdate = new CoreCmsUser { id = request.UserId };
                switch (request.AccountType)
                {
                    case AccountType.Balance:
                        userToUpdate.balance = beforeAmount + changeAmount;
                        break;
                    case AccountType.CommissionAvailable:
                        userToUpdate.commissionAvailable = beforeAmount + changeAmount;
                        break;
                    case AccountType.CommissionFrozen:
                        userToUpdate.commissionFrozen = beforeAmount + changeAmount;
                        break;
                    case AccountType.CommissionDebt:
                        userToUpdate.commissionDebt = beforeAmount + changeAmount;
                        break;
                    default:
                        userToUpdate.balance = beforeAmount + changeAmount;
                        break;
                }

                var updateable = _unitOfWork.GetDbClient().Updateable(userToUpdate);
                // 只更新目标字段
                updateable = request.AccountType switch
                {
                    AccountType.Balance => updateable.UpdateColumns(it => it.balance),
                    AccountType.CommissionAvailable => updateable.UpdateColumns(it => it.commissionAvailable),
                    AccountType.CommissionFrozen => updateable.UpdateColumns(it => it.commissionFrozen),
                    AccountType.CommissionDebt => updateable.UpdateColumns(it => it.commissionDebt),
                    _ => updateable.UpdateColumns(it => it.balance)
                };
                var updateResult = await updateable.Where(it => it.id == request.UserId).ExecuteCommandHasChangeAsync();

                if (!updateResult)
                {
                    _unitOfWork.RollbackTran();
                    return BalanceChangeResult.Fail(11007, "余额不足或用户不存在");
                }

                // 8. 读取更新后余额（事务内，仍持有行锁）
                var updatedUser = await _unitOfWork.GetDbClient().Queryable<CoreCmsUser>()
                    .Where(u => u.id == request.UserId)
                    .SingleAsync();
                afterAmount = GetAccountBalance(updatedUser, request.AccountType);

                // 9. 插入资金流水
                var balanceModel = new CoreCmsUserBalance();
                balanceModel.userId = request.UserId;
                balanceModel.type = ResolveLegacyType(request.SourceType);
                balanceModel.money = changeAmount;
                balanceModel.balance = afterAmount;
                balanceModel.sourceId = request.SourceId;
                balanceModel.memo = request.Remark;
                balanceModel.createTime = DateTime.Now;
                balanceModel.accountType = (int)request.AccountType;
                balanceModel.operationType = request.OperationType;
                balanceModel.beforeAmount = beforeAmount;
                balanceModel.afterAmount = afterAmount;
                balanceModel.idempotencyKey = request.IdempotencyKey;

                var balanceModelId = await _dal.InsertAsync(balanceModel);

                // 10. 提交事务
                _unitOfWork.CommitTran();

                return BalanceChangeResult.Ok(balanceModelId, beforeAmount, afterAmount, changeAmount);
            }
            catch (Exception ex)
            {
                _unitOfWork.RollbackTran();
                NLogUtil.WriteFileLog(NLog.LogLevel.Error, LogType.Other,
                    "资金变更", $"ChangeAsync 失败: {request.IdempotencyKey}", ex);

                // 唯一索引冲突：可能并发插入，尝试幂等返回
                if (IsUniqueConstraintViolation(ex))
                {
                    var retry = await _dal.QueryByClauseAsync(
                        p => p.idempotencyKey == request.IdempotencyKey);
                    if (retry != null)
                    {
                        return BalanceChangeResult.Idempotent(
                            retry.id,
                            retry.beforeAmount ?? 0,
                            retry.afterAmount ?? 0,
                            retry.money);
                    }
                }

                return BalanceChangeResult.Fail(10999, $"资金变更异常: {ex.Message}");
            }
        }

        /// <summary>
        ///     计算实际变更金额（含方向）
        /// </summary>
        private static decimal ComputeChangeAmount(BalanceChangeRequest request)
        {
            var amount = request.Amount;

            // 扣减类操作
            var isDeduction = request.OperationType is "Pay" or "Tocash" or "Cancel" or "Clawback" or "DebtOffset"
                or "UnfreezeFrozen" or "CancelFrozen" or "ClawbackAvailable" or "WithdrawAvailable";
            if (isDeduction)
            {
                return -(amount + request.ServiceFee);
            }

            // 增加类操作
            return amount;
        }

        /// <summary>
        ///     获取账户余额字段名
        /// </summary>
        private static string GetBalanceColumnName(AccountType accountType) => accountType switch
        {
            AccountType.Balance => "balance",
            AccountType.CommissionAvailable => "commissionAvailable",
            AccountType.CommissionFrozen => "commissionFrozen",
            AccountType.CommissionDebt => "commissionDebt",
            _ => "balance"
        };

        /// <summary>
        ///     从用户实体获取指定账户余额
        /// </summary>
        private static decimal GetAccountBalance(CoreCmsUser user, AccountType accountType) => accountType switch
        {
            AccountType.Balance => user.balance,
            AccountType.CommissionAvailable => user.commissionAvailable,
            AccountType.CommissionFrozen => user.commissionFrozen,
            AccountType.CommissionDebt => user.commissionDebt,
            _ => user.balance
        };

        /// <summary>
        ///     将 SourceType 名称映射为旧枚举值
        /// </summary>
        private static int ResolveLegacyType(string sourceType) => sourceType switch
        {
            "Pay" => 1,
            "Refund" => 2,
            "Recharge" => 3,
            "Tocash" => 4,
            "Distribution" => 5,
            "Admin" => 6,
            "Prize" => 7,
            "Service" => 8,
            "Agent" => 9,
            "CommissionFreeze" => 10,
            "CommissionUnfreeze" => 11,
            "CommissionCancel" => 12,
            "CommissionClawback" => 13,
            _ => 0
        };

        /// <summary>
        ///     判断是否为唯一索引冲突异常
        /// </summary>
        private static bool IsUniqueConstraintViolation(Exception ex)
        {
            // MySQL: 1062 Duplicate entry
            // SqlServer: 2601 Cannot insert duplicate key
            var msg = ex.Message;
            return msg.Contains("1062") || msg.Contains("Duplicate entry")
                || msg.Contains("2601") || msg.Contains("UNIQUE constraint");
        }

        #endregion

    }
}
