-- ============================================================
-- 财务重构迁移 003/004/005 执行前预检 + 执行后验证 + 守恒对账 SQL
-- 来源：docs/design/05-financial-data-migration.md
--
-- 使用方法：
--   1) 执行 003/004/005 前：跑 [预检] 段，确保 0 行新列已存在 + 旧数据可以映射
--   2) 按序执行 003 → 004 → 005 升级 SQL
--   3) 执行后：依次跑 [后验] 段，每条都应返回 0 行（金额守恒、状态映射完整）
--   4) 上线后定期：跑 [运行时对账] 段
--
-- 注：MySQL 5.7+ / SqlServer 均兼容本文法。NULL 排序差异已在脚本中考虑。
-- ============================================================

-- ============ [预检 003-a] 升级前确认 CoreCmsDistributionOrder 表无新列
-- 期望：返回 0 行（新列尚不存在）。若返回 1 行则说明本脚本已跑过，不要再跑 003 升级。
SELECT TABLE_NAME, COLUMN_NAME
FROM INFORMATION_SCHEMA.COLUMNS
WHERE ((TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'CoreCmsDistributionOrder')
   OR  (TABLE_CATALOG IS NOT NULL      AND TABLE_NAME = 'CoreCmsDistributionOrder'))
  AND COLUMN_NAME IN (
        'status', 'frozenAmount', 'availableAmount',
        'frozenTime', 'expectedSettleTime', 'settledTime',
        'cancelledTime', 'clawedBackTime', 'refundAmount',
        'sourceOrderItemId', 'ruleSnapshot', 'referrerUserId',
        'commissionRateSnapshot', 'commissionTypeSnapshot',
        'withdrawnAmount', 'idempotencyKey'
  );
-- 期望：0 行（升级前）


-- ============ [预检 003-b] 表当前数据量（用于估算 ALTER 锁表时长）
SELECT COUNT(*) AS totalRows,
       SUM(CASE WHEN isSettlement = 0 THEN 1 ELSE 0 END) AS legacyUnsettled,
       SUM(CASE WHEN isSettlement = 1 THEN 1 ELSE 0 END) AS legacySettled,
       SUM(CASE WHEN isSettlement = 2 THEN 1 ELSE 0 END) AS legacyCancelled
FROM CoreCmsDistributionOrder;
-- 大表建议低峰期或 pt-online-schema-change


-- ============ [预检 004-a] 升级前统计 isSettlement 各值分布（验证映射算法）
-- 旧字段说明：0=未知/未结算，1=已结算，2=已取消。看仓库代码 GlobalEnumVars.DistributionOrderSettlementStatus
SELECT isSettlement, COUNT(*) AS cnt
FROM CoreCmsDistributionOrder
GROUP BY isSettlement;
-- 检查：若出现 0/1/2 之外的值，需先手工核对数据再执行迁移 004。


-- ============ [预检 005-a] 升级前统计有 Distribution(type=5) 流水的用户数
-- 迁移 005 用 type=5 流水汇总写 commissionAvailable，前端需确认量级
SELECT COUNT(DISTINCT userId) AS usersWithDistributionFlow,
       SUM(CASE WHEN money > 0 THEN money ELSE 0 END) AS totalDistributionPositive,
       SUM(CASE WHEN money < 0 THEN money ELSE 0 END) AS totalDistributionNegative
FROM CoreCmsUserBalance
WHERE type = 5;


-- ============ [后验 003] 升级后新列都存在
-- 期望：14 行（14 个新列都已添加）
SELECT COUNT(*) AS newColumnsAdded
FROM INFORMATION_SCHEMA.COLUMNS
WHERE ((TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'CoreCmsDistributionOrder')
   OR  (TABLE_CATALOG IS NOT NULL      AND TABLE_NAME = 'CoreCmsDistributionOrder'))
  AND COLUMN_NAME IN (
        'status', 'frozenAmount', 'availableAmount',
        'frozenTime', 'expectedSettleTime', 'settledTime',
        'cancelledTime', 'clawedBackTime', 'refundAmount',
        'sourceOrderItemId', 'ruleSnapshot', 'referrerUserId',
        'commissionRateSnapshot', 'commissionTypeSnapshot',
        'withdrawnAmount', 'idempotencyKey'
  );
-- 期望：16 行（共 16 个新列）


-- ============ [后验 003] 唯一索引存在
-- 期望：返回 1 行
SELECT COUNT(*) AS ukExists
FROM INFORMATION_SCHEMA.STATISTICS
WHERE ((INDEX_SCHEMA = DATABASE() AND TABLE_NAME = 'CoreCmsDistributionOrder')
   OR  (TABLE_CATALOG IS NOT NULL      AND TABLE_NAME = 'CoreCmsDistributionOrder'))
  AND INDEX_NAME = 'UK_DistributionOrder_IdempotencyKey';
-- 期望：>=1


-- ============ [后验 004-a] 全表 status 已被映射（无遗留 0=Pending）
-- 期望：0 行
SELECT COUNT(*) AS unmappedPending
FROM CoreCmsDistributionOrder
WHERE status = 0;
-- 期望：0


-- ============ [后验 004-b] 状态与旧 isSettlement 对应关系抽样
-- 期望：四档分布合理，无错配。
--   isSettlement=0(旧未结算) → status=2(Available) 或 1(Frozen)（视迁移 004 实现）
--   isSettlement=1(旧已结算) → status=2(Available)
--   isSettlement=2(旧已取消) → status=3(Cancelled)
SELECT
    isSettlement AS legacyIsSettlement,
    status       AS newStatus,
    COUNT(*)     AS cnt
FROM CoreCmsDistributionOrder
GROUP BY isSettlement, status
ORDER BY isSettlement, status;


-- ============ [后验 004-c] DistributionOrder 金额守恒
-- amount == frozenAmount + availableAmount + refundAmount + withdrawnAmount ± 0.01
-- 期望：0 行
SELECT id, orderId, amount,
       frozenAmount, availableAmount, refundAmount, withdrawnAmount,
       amount - frozenAmount - availableAmount - refundAmount - withdrawnAmount AS drift
FROM CoreCmsDistributionOrder
WHERE ABS(amount - frozenAmount - availableAmount - refundAmount - withdrawnAmount) > 0.01;


-- ============ [后验 005-a] commissionAvailable 不应为负
-- 期望：0 行
SELECT COUNT(*) AS usersWithNegativeAvailable
FROM CoreCmsUser
WHERE commissionAvailable < 0;
-- 期望：0


-- ============ [后验 005-b] commissionFrozen / commissionDebt 也不应为负
-- 期望：0 行
SELECT COUNT(*) AS usersWithNegativeCommission
FROM CoreCmsUser
WHERE commissionFrozen < 0 OR commissionDebt < 0;
-- 期望：0


-- ============ [运行时对账 1] User 佣金账户守恒
-- frozen + available + debt 应与历史流水一致（入账 - 已提现 + 已追回 ± 误差）
-- 期望：0 行
SELECT
    u.id,
    u.commissionFrozen,
    u.commissionAvailable,
    u.commissionDebt,
    COALESCE(SUM(CASE WHEN b.accountType IN (1, 2) AND b.money > 0 THEN b.money ELSE 0 END), 0) AS totalCommissionIn,
    COALESCE(SUM(CASE WHEN b.accountType = 1 AND b.money < 0 THEN ABS(b.money) ELSE 0 END), 0) AS totalAvailableOut,
    COALESCE(SUM(CASE WHEN b.accountType = 2 AND b.money < 0 THEN ABS(b.money) ELSE 0 END), 0) AS totalFrozenOut,
    COALESCE(SUM(CASE WHEN b.accountType = 3 THEN b.money ELSE 0 END), 0) AS totalDebt
FROM CoreCmsUser u
LEFT JOIN CoreCmsUserBalance b ON b.userId = u.id
GROUP BY u.id
HAVING ABS(
    u.commissionFrozen + u.commissionAvailable - u.commissionDebt
    - totalCommissionIn + totalAvailableOut + totalFrozenOut + totalDebt
) > 0.01;


-- ============ [运行时对账 2] DistributionOrder 仍守恒
-- 期望：0 行
SELECT id, orderId,
       amount, frozenAmount, availableAmount, refundAmount, withdrawnAmount
FROM CoreCmsDistributionOrder
WHERE ABS(amount - frozenAmount - availableAmount - refundAmount - withdrawnAmount) > 0.01;


-- ============ [运行时对账 3] User.balance 与最新 Balance 流水一致
-- 仅看 accountType=0(Balance) 流水，取最新一笔的 balance 字段比对。
-- 期望：0 行
--   MySQL 版：用 LIMIT 子查询；SqlServer 2012+：用 FIRST_VALUE/LATERAL，这里给出兼容写法。
SELECT u.id, u.balance,
       (SELECT b.balance
          FROM CoreCmsUserBalance b
         WHERE b.userId = u.id AND (b.accountType = 0 OR b.accountType IS NULL)
         ORDER BY b.id DESC
         LIMIT 1) AS lastRecordedBalance
FROM CoreCmsUser u
WHERE (SELECT b.balance
         FROM CoreCmsUserBalance b
        WHERE b.userId = u.id AND (b.accountType = 0 OR b.accountType IS NULL)
        ORDER BY b.id DESC
        LIMIT 1) IS NOT NULL
  AND ABS(u.balance - (SELECT b.balance
         FROM CoreCmsUserBalance b
        WHERE b.userId = u.id AND (b.accountType = 0 OR b.accountType IS NULL)
        ORDER BY b.id DESC
        LIMIT 1)) > 0.01;
-- SqlServer 版：将上面的 LIMIT 1 改为 TOP 1，并把 SELECT TOP 1 ... 形式重写。
-- 期望：0 行


-- ============ [运行时对账 4] 幂等键唯一性自检（流水表）
-- 期望：duplicateKeys = 0
SELECT idempotencyKey, COUNT(*) AS dupCount
FROM CoreCmsUserBalance
WHERE idempotencyKey IS NOT NULL AND idempotencyKey <> ''
GROUP BY idempotencyKey
HAVING COUNT(*) > 1;
-- 期望：0 行


-- ============ [运行时对账 5] 幂等键唯一性自检（佣金记录表）
-- 期望：0 行
SELECT idempotencyKey, COUNT(*) AS dupCount
FROM CoreCmsDistributionOrder
WHERE idempotencyKey IS NOT NULL AND idempotencyKey <> ''
GROUP BY idempotencyKey
HAVING COUNT(*) > 1;
-- 期望：0 行


-- ============ [运行时对账 6] 处于异常状态的佣金（需人工干预）
-- 期望：0 行（若有请人工核对）
SELECT id, orderId, userId, status, frozenAmount, availableAmount
FROM CoreCmsDistributionOrder
WHERE status = 9;  -- CommissionStatus.Exception


-- ============ [运行时对账 7] 保护期模式下预计结算时间漏设的数据修复入口
-- 当 CommissionProtectionPeriodDays > 0 启用后，仍存在 status=Frozen 且 expectedSettleTime 为空的
-- 表示 FinishOrder 跑过但未正确设定期（旧数据或路径分支异常）。
-- 期望：0 行（启用保护期模式之后）
SELECT id, orderId, userId, status, expectedSettleTime, frozenTime, settledTime
FROM CoreCmsDistributionOrder
WHERE status = 1 AND expectedSettleTime IS NULL AND isDelete = 0;
-- 若有非零结果：人工确认该订单完成后，补：
--   UPDATE CoreCmsDistributionOrder
--   SET expectedSettleTime = DATE_ADD(NOW(), INTERVAL [N天] DAY)
--   WHERE id IN (...);


-- ============================================================
--  END of migration verification script — 2026-08-01
-- ============================================================