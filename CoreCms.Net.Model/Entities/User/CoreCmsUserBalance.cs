/***********************************************************************
 *            Project: CoreCms
 *        ProjectName: 核心内容管理系统                                
 *                Web: https://www.corecms.net                      
 *             Author: 大灰灰                                          
 *              Email: jianweie@163.com
 *         CreateTime: 2021-06-08 22:14:59
 *        Description: 暂无
***********************************************************************/ 
using SqlSugar;
using System.ComponentModel.DataAnnotations;

namespace CoreCms.Net.Model.Entities
{
    /// <summary>
    /// 用户余额表
    /// </summary>
    [SugarTable("CoreCmsUserBalance",TableDescription = "用户余额表")]
    public partial class CoreCmsUserBalance
    {
        /// <summary>
        /// 用户余额表
        /// </summary>
        public CoreCmsUserBalance()
        {
        }

        /// <summary>
        /// 序列
        /// </summary>
        [Display(Name = "序列")]
        [SugarColumn(ColumnDescription = "序列", IsPrimaryKey = true, IsIdentity = true)]
        [Required(ErrorMessage = "请输入{0}")]
        public System.Int32 id { get; set; }
        /// <summary>
        /// 用户id
        /// </summary>
        [Display(Name = "用户id")]
        [SugarColumn(ColumnDescription = "用户id")]
        [Required(ErrorMessage = "请输入{0}")]
        public System.Int32 userId { get; set; }
        /// <summary>
        /// 类型
        /// </summary>
        [Display(Name = "类型")]
        [SugarColumn(ColumnDescription = "类型")]
        [Required(ErrorMessage = "请输入{0}")]
        public System.Int32 type { get; set; }
        /// <summary>
        /// 金额
        /// </summary>
        [Display(Name = "金额")]
        [SugarColumn(ColumnDescription = "金额")]
        [Required(ErrorMessage = "请输入{0}")]
        public System.Decimal money { get; set; }
        /// <summary>
        /// 余额
        /// </summary>
        [Display(Name = "余额")]
        [SugarColumn(ColumnDescription = "余额")]
        [Required(ErrorMessage = "请输入{0}")]
        public System.Decimal balance { get; set; }
        /// <summary>
        /// 资源id
        /// </summary>
        [Display(Name = "资源id")]
        [SugarColumn(ColumnDescription = "资源id", IsNullable = true)]
        [StringLength(50, ErrorMessage = "【{0}】不能超过{1}字符长度")]
        public System.String sourceId { get; set; }
        /// <summary>
        /// 描述
        /// </summary>
        [Display(Name = "描述")]
        [SugarColumn(ColumnDescription = "描述", IsNullable = true)]
        [StringLength(200, ErrorMessage = "【{0}】不能超过{1}字符长度")]
        public System.String memo { get; set; }
        /// <summary>
        /// 创建时间
        /// </summary>
        [Display(Name = "创建时间")]
        [SugarColumn(ColumnDescription = "创建时间")]
        [Required(ErrorMessage = "请输入{0}")]
        public System.DateTime createTime { get; set; }

        // ===== M4 新增字段：资金流水幂等与账户类型 =====

        /// <summary>
        /// 账户类型 0=Balance 1=CommissionAvailable 2=CommissionFrozen 3=CommissionDebt
        /// </summary>
        [Display(Name = "账户类型")]
        [SugarColumn(ColumnDescription = "账户类型", IsNullable = true)]
        public System.Int32? accountType { get; set; }

        /// <summary>
        /// 操作类型（如 Settle、Freeze、Unfreeze、Cancel、Clawback 等）
        /// </summary>
        [Display(Name = "操作类型")]
        [SugarColumn(ColumnDescription = "操作类型", IsNullable = true)]
        [StringLength(30, ErrorMessage = "【{0}】不能超过{1}字符长度")]
        public System.String operationType { get; set; }

        /// <summary>
        /// 变更前金额
        /// </summary>
        [Display(Name = "变更前金额")]
        [SugarColumn(ColumnDescription = "变更前金额", IsNullable = true)]
        public System.Decimal? beforeAmount { get; set; }

        /// <summary>
        /// 变更后金额
        /// </summary>
        [Display(Name = "变更后金额")]
        [SugarColumn(ColumnDescription = "变更后金额", IsNullable = true)]
        public System.Decimal? afterAmount { get; set; }

        /// <summary>
        /// 幂等键，非 NULL 值唯一
        /// </summary>
        [Display(Name = "幂等键")]
        [SugarColumn(ColumnDescription = "幂等键", IsNullable = true)]
        [StringLength(100, ErrorMessage = "【{0}】不能超过{1}字符长度")]
        public System.String idempotencyKey { get; set; }
    }
}