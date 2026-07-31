namespace CoreCms.Net.Model.ViewModels.Financial
{
    /// <summary>
    /// 资金变更请求
    /// </summary>
    public sealed class BalanceChangeRequest
    {
        /// <summary>
        /// 用户ID
        /// </summary>
        public int UserId { get; init; }

        /// <summary>
        /// 账户类型
        /// </summary>
        public AccountType AccountType { get; init; } = AccountType.Balance;

        /// <summary>
        /// 变更金额（永远为正数，方向由 OperationType 决定）
        /// </summary>
        public decimal Amount { get; init; }

        /// <summary>
        /// 来源类型（对应 UserBalanceSourceTypes 的名称，如 "Pay", "Refund", "Distribution"）
        /// </summary>
        public string SourceType { get; init; } = string.Empty;

        /// <summary>
        /// 来源ID（如 paymentId、orderId、tocashId）
        /// </summary>
        public string SourceId { get; init; } = string.Empty;

        /// <summary>
        /// 操作类型（如 Settle、Freeze、Unfreeze、Cancel、Clawback 等）
        /// </summary>
        public string OperationType { get; init; } = string.Empty;

        /// <summary>
        /// 幂等键，不能为空。格式：{OperationType}:{SourceId}[:{UserId}]
        /// </summary>
        public string IdempotencyKey { get; init; } = string.Empty;

        /// <summary>
        /// 服务费金额（仅提现场景）
        /// </summary>
        public decimal ServiceFee { get; init; }

        /// <summary>
        /// 备注
        /// </summary>
        public string Remark { get; init; } = string.Empty;
    }
}
