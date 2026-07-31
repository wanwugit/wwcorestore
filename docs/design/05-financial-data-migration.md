# 05 财务数据迁移

## 一、迁移原则

1. 每个迁移脚本必须包含**升级 SQL** 和**回滚 SQL**
2. 旧字段保留，新增字段有默认值，不破坏现有数据
3. 旧数据通过数据迁移脚本逐步映射到新字段
4. 迁移脚本按序号执行，不可跳过
5. 每个脚本执行前检查前置条件，执行后验证结果

---

## 二、迁移脚本

### 001：CoreCmsUser 新增佣金账户字段

```sql
-- ============================================
-- 迁移 001：CoreCmsUser 新增佣金账户字段
-- 日期：待定
-- 前置：无
-- ============================================

-- 升级 SQL
ALTER TABLE CoreCmsUser
    ADD COLUMN commissionAvailable DECIMAL(18,2) NOT NULL DEFAULT 0 COMMENT '佣金可提现余额',
    ADD COLUMN commissionFrozen    DECIMAL(18,2) NOT NULL DEFAULT 0 COMMENT '佣金冻结余额',
    ADD COLUMN commissionDebt      DECIMAL(18,2) NOT NULL DEFAULT 0 COMMENT '佣金负债';

-- 执行后验证
SELECT COUNT(*) FROM CoreCmsUser
WHERE commissionAvailable != 0 OR commissionFrozen != 0 OR commissionDebt != 0;
-- 预期：0（所有新字段默认为0）

-- 回滚 SQL
ALTER TABLE CoreCmsUser
    DROP COLUMN commissionAvailable,
    DROP COLUMN commissionFrozen,
    DROP COLUMN commissionDebt;
```

### 002：CoreCmsUserBalance 新增幂等和账户类型字段

```sql
-- ============================================
-- 迁移 002：CoreCmsUserBalance 新增幂等键和账户类型
-- 日期：待定
-- 前置：001
-- ============================================

-- 升级 SQL
ALTER TABLE CoreCmsUserBalance
    ADD COLUMN idempotencyKey VARCHAR(100) NULL COMMENT '幂等键',
    ADD COLUMN accountType    INT NOT NULL DEFAULT 0 COMMENT '账户类型 0=Balance 1=CommissionAvailable 2=CommissionFrozen 3=CommissionDebt';

-- 唯一索引（idempotencyKey 允许 NULL，非 NULL 值唯一）
CREATE UNIQUE INDEX UK_UserBalance_IdempotencyKey
ON CoreCmsUserBalance (idempotencyKey);

-- 执行后验证
SELECT COUNT(*) FROM CoreCmsUserBalance WHERE accountType != 0;
-- 预期：0（所有旧流水 accountType=0）

-- 回滚 SQL
DROP INDEX UK_UserBalance_IdempotencyKey ON CoreCmsUserBalance;
ALTER TABLE CoreCmsUserBalance
    DROP COLUMN idempotencyKey,
    DROP COLUMN accountType;
```

### 003：CoreCmsDistributionOrder 新增佣金状态字段

```sql
-- ============================================
-- 迁移 003：CoreCmsDistributionOrder 新增佣金状态字段
-- 日期：待定
-- 前置：002
-- ============================================

-- 升级 SQL
ALTER TABLE CoreCmsDistributionOrder
    ADD COLUMN status                  INT     NOT NULL DEFAULT 0 COMMENT '佣金状态',
    ADD COLUMN frozenAmount            DECIMAL(18,2) NOT NULL DEFAULT 0 COMMENT '冻结金额',
    ADD COLUMN availableAmount         DECIMAL(18,2) NOT NULL DEFAULT 0 COMMENT '可提现金额',
    ADD COLUMN frozenTime              DATETIME NULL COMMENT '冻结时间',
    ADD COLUMN expectedSettleTime      DATETIME NULL COMMENT '预计结算时间',
    ADD COLUMN settledTime             DATETIME NULL COMMENT '实际结算时间',
    ADD COLUMN cancelledTime           DATETIME NULL COMMENT '取消时间',
    ADD COLUMN clawedBackTime          DATETIME NULL COMMENT '追回时间',
    ADD COLUMN refundAmount            DECIMAL(18,2) NOT NULL DEFAULT 0 COMMENT '退款追回金额',
    ADD COLUMN sourceOrderItemId       INT     NULL COMMENT '来源订单明细ID',
    ADD COLUMN ruleSnapshot            VARCHAR(500) NULL COMMENT '佣金规则快照JSON',
    ADD COLUMN referrerUserId          INT     NULL COMMENT '推荐人用户ID快照',
    ADD COLUMN commissionRateSnapshot  DECIMAL(18,4) NULL COMMENT '佣金比例快照',
    ADD COLUMN commissionTypeSnapshot  INT     NULL COMMENT '佣金类型快照',
    ADD COLUMN withdrawnAmount         DECIMAL(18,2) NOT NULL DEFAULT 0 COMMENT '累计已提现金额',
    ADD COLUMN idempotencyKey          VARCHAR(100) NULL COMMENT '幂等键';

-- 唯一索引
CREATE UNIQUE INDEX UK_DistributionOrder_IdempotencyKey
ON CoreCmsDistributionOrder (idempotencyKey);

-- 回滚 SQL
DROP INDEX UK_DistributionOrder_IdempotencyKey ON CoreCmsDistributionOrder;
ALTER TABLE CoreCmsDistributionOrder
    DROP COLUMN status,
    DROP COLUMN frozenAmount,
    DROP COLUMN availableAmount,
    DROP COLUMN frozenTime,
    DROP COLUMN expectedSettleTime,
    DROP COLUMN settledTime,
    DROP COLUMN cancelledTime,
    DROP COLUMN clawedBackTime,
    DROP COLUMN refundAmount,
    DROP COLUMN sourceOrderItemId,
    DROP COLUMN ruleSnapshot,
    DROP COLUMN referrerUserId,
    DROP COLUMN commissionRateSnapshot,
    DROP COLUMN commissionTypeSnapshot,
    DROP COLUMN withdrawnAmount,
    DROP COLUMN idempotencyKey;
```

### 004：旧数据映射 — DistributionOrder 状态

```sql
-- ============================================
-- 迁移 004：将旧 isSettlement 映射到新 status
-- 日期：待定
-- 前置：003
-- ============================================

-- 升级 SQL

-- isSettlement=0 (未结算) → status=2 (Available)
-- 因为旧系统中"未结算"意味着订单已完成，佣金可以直接提现
UPDATE CoreCmsDistributionOrder
SET status = 2,
    frozenAmount = 0,
    availableAmount = amount,
    settledTime = updateTime
WHERE isSettlement = 0
  AND status = 0;  -- 只更新尚未映射的记录

-- isSettlement=1 (已结算) → status=2 (Available)
-- 旧系统中"已结算"意味着钱已经转入余额
UPDATE CoreCmsDistributionOrder
SET status = 2,
    frozenAmount = 0,
    availableAmount = 0,  -- 已转入余额，可提现余额为0
    settledTime = updateTime
WHERE isSettlement = 1
  AND status = 0;

-- isSettlement=2 (已取消) → status=3 (Cancelled)
UPDATE CoreCmsDistributionOrder
SET status = 3,
    frozenAmount = 0,
    availableAmount = 0,
    cancelledTime = updateTime
WHERE isSettlement = 2
  AND status = 0;

-- 执行后验证
SELECT COUNT(*) FROM CoreCmsDistributionOrder WHERE status = 0;
-- 预期：0（所有记录已映射）

-- 验证金额一致性
SELECT id, orderId, amount, frozenAmount, availableAmount, refundAmount
FROM CoreCmsDistributionOrder
WHERE ABS(amount - frozenAmount - availableAmount - refundAmount) > 0.01;
-- 预期：0行（金额守恒）

-- 回滚 SQL
UPDATE CoreCmsDistributionOrder
SET status = 0,
    frozenAmount = 0,
    availableAmount = 0,
    settledTime = NULL,
    cancelledTime = NULL;
```

### 005：旧数据映射 — User 佣金余额

```sql
-- ============================================
-- 迁移 005：将旧佣金流水汇总写入 commissionAvailable
-- 日期：待定
-- 前置：001, 004
-- ============================================

-- 升级 SQL

-- 计算每个用户的佣金净额（已入账 - 已提现）
-- 旧系统中佣金通过 Distribution=5 入账，提现通过 Tocash=4 扣减
-- 但旧系统佣金和充值混在同一个 balance 中，无法精确拆分

-- 方案：用 Distribution 流水正数之和作为 commissionAvailable
-- （因为旧系统中已结算的佣金已经通过 Change(type=Distribution) 入账到 balance）
-- 提现部分无法区分是佣金提现还是余额提现，暂不处理

UPDATE CoreCmsUser u
SET commissionAvailable = COALESCE((
    SELECT SUM(CASE WHEN b.type = 5 THEN b.money ELSE 0 END)
    FROM CoreCmsUserBalance b
    WHERE b.userId = u.id
), 0)
WHERE EXISTS (
    SELECT 1 FROM CoreCmsUserBalance b
    WHERE b.userId = u.id AND b.type = 5
);

-- 执行后验证
SELECT COUNT(*) FROM CoreCmsUser WHERE commissionAvailable < 0;
-- 预期：0

-- 回滚 SQL
UPDATE CoreCmsUser
SET commissionAvailable = 0,
    commissionFrozen = 0,
    commissionDebt = 0;
```

### 006：CoreCmsUser 新增密码版本字段

```sql
-- ============================================
-- 迁移 006：密码渐进升级支持
-- 日期：待定
-- 前置：无
-- ============================================

-- 升级 SQL
ALTER TABLE CoreCmsUser
    ADD COLUMN passwordVersion    INT     NOT NULL DEFAULT 0 COMMENT '密码版本 0=旧MD5 1=PBKDF2',
    ADD COLUMN passwordMigratedAt DATETIME NULL COMMENT '密码迁移时间';

-- SysUser 也需要同样处理
ALTER TABLE SysUser
    ADD COLUMN passwordVersion    INT     NOT NULL DEFAULT 0 COMMENT '密码版本 0=明文 1=PBKDF2',
    ADD COLUMN passwordMigratedAt DATETIME NULL COMMENT '密码迁移时间';

-- 执行后验证
SELECT COUNT(*) FROM CoreCmsUser WHERE passwordVersion != 0;
-- 预期：0（所有旧密码 version=0）

-- 回滚 SQL
ALTER TABLE CoreCmsUser
    DROP COLUMN passwordVersion,
    DROP COLUMN passwordMigratedAt;

ALTER TABLE SysUser
    DROP COLUMN passwordVersion,
    DROP COLUMN passwordMigratedAt;
```

---

## 三、迁移执行顺序

```
001 → 002 → 003 → 004 → 005 → 006
                        ↑
                     依赖 001
```

004 和 005 依赖前面的结构变更，但彼此独立。

---

## 四、迁移风险

| 风险 | 影响 | 缓解 |
|------|------|------|
| 004 映射错误 | 佣金状态不正确 | 执行前备份 DistributionOrder 表；映射后人工抽检 |
| 005 佣金余额计算不准 | commissionAvailable 与实际不符 | 旧系统佣金和充值混合，无法完美拆分；上线后通过财务对账修正 |
| 大表 ALTER TABLE 锁表 | 线上服务不可用 | 使用 pt-online-schema-change（MySQL）或低峰期执行 |
| 回滚后新字段数据丢失 | 新业务数据丢失 | 回滚前备份新字段数据到临时表 |

---

## 五、数据一致性对账脚本

上线后定期执行，检查财务不变量：

```sql
-- 对账 1：每个用户的佣金守恒
-- frozen + available + withdrawn + clawedBack + debt 应等于历史佣金总额
SELECT
    u.id,
    u.commissionFrozen,
    u.commissionAvailable,
    u.commissionDebt,
    COALESCE(SUM(CASE WHEN b.type IN (5, 10) THEN b.money ELSE 0 END), 0) AS totalCommissionIn,
    COALESCE(SUM(CASE WHEN b.type = 4 THEN ABS(b.money) ELSE 0 END), 0) AS totalWithdrawn,
    COALESCE(SUM(CASE WHEN b.type IN (12, 13) THEN ABS(b.money) ELSE 0 END), 0) AS totalClawedBack
FROM CoreCmsUser u
LEFT JOIN CoreCmsUserBalance b ON b.userId = u.id
GROUP BY u.id
HAVING ABS(
    u.commissionFrozen + u.commissionAvailable + u.commissionDebt
    - totalCommissionIn + totalWithdrawn + totalClawedBack
) > 0.01;

-- 对账 2：DistributionOrder 金额守恒
SELECT id, orderId, amount, frozenAmount, availableAmount, refundAmount, withdrawnAmount
FROM CoreCmsDistributionOrder
WHERE ABS(amount - frozenAmount - availableAmount - refundAmount - withdrawnAmount) > 0.01;

-- 对账 3：User.balance 与最新流水一致
SELECT u.id, u.balance,
    (SELECT b.balance FROM CoreCmsUserBalance b
     WHERE b.userId = u.id AND b.accountType = 0
     ORDER BY b.id DESC LIMIT 1) AS lastRecordedBalance
FROM CoreCmsUser u
HAVING ABS(u.balance - lastRecordedBalance) > 0.01;
```
