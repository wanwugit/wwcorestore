namespace CoreCms.Net.Model.ViewModels.Financial
{
    /// <summary>
    /// 资金变更结果
    /// </summary>
    public sealed class BalanceChangeResult
    {
        /// <summary>
        /// 是否成功
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// 错误代码（成功时为 0）
        /// </summary>
        public int ErrorCode { get; init; }

        /// <summary>
        /// 错误消息
        /// </summary>
        public string ErrorMessage { get; init; } = string.Empty;

        /// <summary>
        /// 变更前金额
        /// </summary>
        public decimal BeforeAmount { get; init; }

        /// <summary>
        /// 变更后金额
        /// </summary>
        public decimal AfterAmount { get; init; }

        /// <summary>
        /// 实际变更金额（含方向，正为增加，负为减少）
        /// </summary>
        public decimal ChangeAmount { get; init; }

        /// <summary>
        /// 流水ID
        /// </summary>
        public long BalanceRecordId { get; init; }

        /// <summary>
        /// 是否为幂等返回（重复请求，返回已有结果）
        /// </summary>
        public bool IsIdempotentReturn { get; init; }

        /// <summary>
        /// 创建成功结果
        /// </summary>
        public static BalanceChangeResult Ok(long recordId, decimal before, decimal after, decimal change)
        {
            return new BalanceChangeResult
            {
                Success = true,
                ErrorCode = 0,
                BeforeAmount = before,
                AfterAmount = after,
                ChangeAmount = change,
                BalanceRecordId = recordId
            };
        }

        /// <summary>
        /// 创建幂等返回结果
        /// </summary>
        public static BalanceChangeResult Idempotent(long recordId, decimal before, decimal after, decimal change)
        {
            return new BalanceChangeResult
            {
                Success = true,
                ErrorCode = 0,
                BeforeAmount = before,
                AfterAmount = after,
                ChangeAmount = change,
                BalanceRecordId = recordId,
                IsIdempotentReturn = true
            };
        }

        /// <summary>
        /// 创建失败结果
        /// </summary>
        public static BalanceChangeResult Fail(int errorCode, string errorMessage)
        {
            return new BalanceChangeResult
            {
                Success = false,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage
            };
        }
    }
}
