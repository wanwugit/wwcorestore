namespace CoreCms.Net.Model.ViewModels.Financial
{
    /// <summary>
    /// 账户类型
    /// </summary>
    public enum AccountType
    {
        /// <summary>普通余额（充值、退款、后台调整）</summary>
        Balance = 0,

        /// <summary>佣金可提现余额</summary>
        CommissionAvailable = 1,

        /// <summary>佣金冻结余额</summary>
        CommissionFrozen = 2,

        /// <summary>佣金负债（已提现但需追回）</summary>
        CommissionDebt = 3
    }
}
