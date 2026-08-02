/***********************************************************************
 *            Project: CoreCms.Net                                     *
 *                Web: https://CoreCms.Net                             *
 *        ProjectName: 核心内容管理系统                                *
 *             Author: 大灰灰                                          *
 *              Email: JianWeie@163.com                                *
 *         CreateTime: 2026-08-01
 *        Description: 佣金定时结算任务
 ***********************************************************************/

using CoreCms.Net.IServices;
using CoreCms.Net.Loging;

namespace CoreCms.Net.Task
{
    /// <summary>
    /// 佣金定时结算任务（Hangfire RecurringJob）
    /// 每小时扫描 status=Frozen 且 expectedSettleTime 已到期的佣金，
    /// 校验订单无进行中售后后，逐笔解冻入账。
    /// 设计文档：docs/design/04-commission-state-machine.md §7。
    /// 幂等保证：SettleSingleCommission 内部状态守卫 + 幂等键。
    /// </summary>
    public class CommissionSettlementJob
    {
        private readonly ICoreCmsDistributionOrderServices _distributionOrderServices;

        public CommissionSettlementJob(ICoreCmsDistributionOrderServices distributionOrderServices)
        {
            _distributionOrderServices = distributionOrderServices;
        }

        public async System.Threading.Tasks.Task Execute()
        {
            var count = await _distributionOrderServices.SettleDueCommissions();
            if (count > 0)
            {
                NLogUtil.WriteFileLog(NLog.LogLevel.Info, LogType.Other,
                    "佣金定时结算", $"本次结算到期佣金 {count} 笔", null);
            }
        }
    }
}