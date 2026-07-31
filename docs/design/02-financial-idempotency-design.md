# 02 财务幂等设计

## 一、幂等键生成规则

### 1.1 格式定义

```
{操作类型}:{业务标识1}:{业务标识2}[:{业务标识N}]
```

- 各段用冒号分隔
- 全部使用英文，不使用中文
- 业务标识取自业务表主键，不使用自增 ID

### 1.2 各操作的幂等键

| 操作 | 幂等键格式 | 示例 |
|------|-----------|------|
| 余额支付 | `Pay:{paymentId}` | `Pay:PAY20260711001` |
| 余额退款 | `Refund:{refundId}` | `Refund:REF20260711001` |
| 在线充值 | `Recharge:{paymentId}` | `Recharge:PAY20260711001` |
| 提现扣款 | `Tocash:{tocashId}` | `Tocash:42` |
| 佣金冻结 | `CommissionFreeze:{orderId}:{userId}` | `CommissionFreeze:ORD001:5` |
| 佣金解冻 | `CommissionUnfreeze:{orderId}:{userId}` | `CommissionUnfreeze:ORD001:5` |
| 佣金结算入账 | `CommissionSettle:{orderId}:{userId}` | `CommissionSettle:ORD001:5` |
| 佣金取消 | `CommissionCancel:{orderId}:{userId}` | `CommissionCancel:ORD001:5` |
| 佣金追回 | `CommissionClawback:{refundId}:{userId}` | `CommissionClawback:REF001:5` |
| 佣金负债 | `CommissionDebt:{refundId}:{userId}` | `CommissionDebt:REF001:5` |
| 负债抵扣 | `CommissionDebtOffset:{commissionSettleId}` | `CommissionDebtOffset:CS42` |
| 后台调整 | `Admin:{operatorId}:{timestamp}` | `Admin:1:1720700000` |
| 代理结算 | `AgentSettle:{orderId}:{userId}` | `AgentSettle:ORD001:3` |

### 1.3 幂等键唯一性

同一笔业务操作可能影响多个用户（如一笔订单产生多级佣金），因此 **幂等键必须包含 userId**。

一笔订单只有一级佣金时，只有一个幂等键。未来如果需要支持多级（虽然本项目不做），幂等键中已有 userId 区分。

---

## 二、数据库唯一约束

### 2.1 CoreCmsUserBalance 表新增字段

| 字段 | 类型 | 说明 |
|------|------|------|
| `idempotencyKey` | varchar(100) | 幂等键，可为 NULL |
| `accountType` | int | 账户类型，默认 0 |

### 2.2 唯一索引

```sql
CREATE UNIQUE INDEX UK_UserBalance_IdempotencyKey
ON CoreCmsUserBalance (idempotencyKey);
```

**设计决策**：`idempotencyKey` 允许 NULL（旧流水无此字段），但非 NULL 值必须唯一。

### 2.3 幂等检查逻辑

```csharp
// 在事务内执行
if (!string.IsNullOrEmpty(idempotencyKey))
{
    var existing = await _dal.QueryByClauseAsync(
        p => p.idempotencyKey == idempotencyKey);

    if (existing != null)
    {
        // 幂等返回：返回与上次相同的结果
        jm.status = true;
        jm.data = existing;
        jm.msg = "幂等返回";
        return jm;
    }
}
```

### 2.4 并发下唯一索引兜底

如果两个请求同时通过幂等检查（极端并发），数据库唯一索引会阻止第二次插入：

```csharp
try
{
    var balanceModelId = await _dal.InsertAsync(balanceModel);
}
catch (Exception ex) when (IsUniqueConstraintViolation(ex))
{
    // 唯一索引冲突，说明已经被其他请求处理
    _unitOfWork.RollbackTran();
    var existing = await _dal.QueryByClauseAsync(
        p => p.idempotencyKey == idempotencyKey);
    jm.status = true;
    jm.data = existing;
    return jm;
}
```

---

## 三、支付回调幂等

### 3.1 当前风险

微信支付回调可能重复发送同一笔支付结果。当前代码通过 `billPaymentInfo.status` 判断是否已处理，但这个检查与后续业务操作不在同一事务中。

### 3.2 改造方案

支付回调处理入口增加幂等键：

```
WeChatPay:{outTradeNo}
AliPay:{outTradeNo}
```

在更新支付单状态之前，先检查支付单状态：
- 如果已经是"已支付"，直接返回成功（微信期望收到成功响应）
- 更新支付单状态使用条件更新：`UPDATE BillPayments SET status = 1 WHERE paymentId = @id AND status = 0`
- affected rows = 0 表示已处理，直接返回

这不需要新增字段，利用现有状态字段的条件更新即可实现幂等。

---

## 四、退款回调幂等

### 4.1 幂等键

```
RefundCallback:{refundId}
```

### 4.2 实现方式

退款单表使用 `status` 字段的条件更新：
```sql
UPDATE CoreCmsBillRefund
SET status = @newStatus
WHERE refundId = @id
  AND status = @expectedOldStatus;
```

affected rows = 0 → 已处理，幂等返回。

---

## 五、FinishOrder 幂等

### 5.1 当前状态

`FinishOrder()` 本身有一定的幂等性（查询 `isSettlement = 0` 的记录），但：

1. 查询和更新之间没有事务
2. 如果 `Change()` 成功但 `UpdateAsync` 失败，钱已入账但标记未更新

### 5.2 改造方案

见文档 04 的 FinishOrder 改造部分。核心：每笔佣金的结算使用幂等键 `CommissionSettle:{orderId}:{userId}`，通过 `Change()` 的幂等机制保证不会重复入账。

---

## 六、提现审核幂等

### 6.1 幂等键

```
TocashAudit:{tocashId}:{action}
```

其中 `action` 为 `approve` / `reject` / `pay` / `fail`。

### 6.2 实现

审核操作使用条件更新：
```sql
UPDATE CoreCmsUserTocash
SET status = @newStatus, updateTime = NOW()
WHERE id = @tocashId
  AND status = @expectedOldStatus;
```

affected rows = 0 → 状态已变更（可能被其他人审核），返回错误。
