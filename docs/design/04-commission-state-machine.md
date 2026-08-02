# 04 佣金状态机

## 一、佣金状态定义

```csharp
public enum CommissionStatus
{
    /// <summary>待处理（订单已创建，尚未支付）</summary>
    Pending = 0,

    /// <summary>已冻结（订单已支付，佣金冻结中）</summary>
    Frozen = 1,

    /// <summary>可提现（售后保护期结束，可提现）</summary>
    Available = 2,

    /// <summary>已取消（退款时佣金仍在冻结，直接取消）</summary>
    Cancelled = 3,

    /// <summary>已追回（退款时佣金已解冻，从可提现中追回）</summary>
    ClawedBack = 4,

    /// <summary>异常（需人工处理）</summary>
    Exception = 9
}
```

**不设 PaidOut 状态**：一笔佣金可能被多次部分提现，提现状态由提现记录维护，佣金记录只记录 `withdrawnAmount`（累计已提现金额）。

---

## 二、状态机

```
                    ┌──────────────────────────────────────────┐
                    │                                          │
                    ▼                                          │
  [Pending] ──支付成功──▶ [Frozen] ──售后保护期结束──▶ [Available]
      │                    │    │                          │   │
      │                    │    │                          │   │
      │                 退款  退款                     退款  提现
      │                    │    │                          │   │
      │                    │    │ 不足部分               │   │
      │                    ▼    ▼ 记负债                 ▼   │
      │              [Cancelled] [ClawedBack]         [ClawedBack]
      │                                              + 负债记录
      │
   订单取消
      │
      ▼
  (不产生佣金记录)
```

### 合法状态转换

| 当前状态 | 事件 | 目标状态 | 条件 | 操作 |
|---------|------|---------|------|------|
| Pending | 订单支付成功 | Frozen | 推荐人存在且佣金规则有效 | 创建佣金记录，冻结金额 |
| Pending | 订单取消 | — | — | 不产生佣金记录 |
| Frozen | 售后保护期结束 | Available | 无进行中的售后 | commissionFrozen → commissionAvailable |
| Frozen | 整单退款 | Cancelled | — | commissionFrozen 扣减 |
| Frozen | 部分退款 | Cancelled | 退全部佣金 | commissionFrozen 扣减（按比例） |
| Available | 提现 | Available | — | commissionAvailable 扣减，记录 withdrawnAmount |
| Available | 退款 | ClawedBack | commissionAvailable >= 追回金额 | commissionAvailable 扣减 |
| Available | 退款 | ClawedBack + 负债 | commissionAvailable < 追回金额 | 扣减可提现，差额记 commissionDebt |
| Cancelled | — | — | 终态 | — |
| ClawedBack | — | — | 终态 | — |
| Exception | 人工处理 | 任意 | — | 人工干预 |

---

## 三、CoreCmsDistributionOrder 新增字段

```sql
ALTER TABLE CoreCmsDistributionOrder
    -- 新状态字段（替代 isSettlement）
    ADD COLUMN status              INT     NOT NULL DEFAULT 0 COMMENT '佣金状态 0=Pending 1=Frozen 2=Available 3=Cancelled 4=ClawedBack 9=Exception',

    -- 冻结与可提现金额
    ADD COLUMN frozenAmount        DECIMAL(18,2) NOT NULL DEFAULT 0 COMMENT '冻结金额',
    ADD COLUMN availableAmount     DECIMAL(18,2) NOT NULL DEFAULT 0 COMMENT '可提现金额',

    -- 时间节点
    ADD COLUMN frozenTime          DATETIME NULL COMMENT '冻结时间',
    ADD COLUMN expectedSettleTime  DATETIME NULL COMMENT '预计结算时间',
    ADD COLUMN settledTime         DATETIME NULL COMMENT '实际结算时间',
    ADD COLUMN cancelledTime       DATETIME NULL COMMENT '取消时间',
    ADD COLUMN clawedBackTime      DATETIME NULL COMMENT '追回时间',

    -- 退款相关
    ADD COLUMN refundAmount        DECIMAL(18,2) NOT NULL DEFAULT 0 COMMENT '退款追回金额',
    ADD COLUMN sourceOrderItemId   INT     NULL COMMENT '来源订单明细ID',

    -- 快照
    ADD COLUMN ruleSnapshot        VARCHAR(500) NULL COMMENT '佣金规则快照JSON',
    ADD COLUMN referrerUserId      INT     NULL COMMENT '推荐人用户ID快照',
    ADD COLUMN commissionRateSnapshot DECIMAL(18,4) NULL COMMENT '佣金比例快照',
    ADD COLUMN commissionTypeSnapshot INT    NULL COMMENT '佣金类型快照 0=固定 1=百分比',

    -- 提现跟踪
    ADD COLUMN withdrawnAmount     DECIMAL(18,2) NOT NULL DEFAULT 0 COMMENT '累计已提现金额',

    -- 幂等
    ADD COLUMN idempotencyKey      VARCHAR(100) NULL COMMENT '幂等键';
```

### 唯一索引

```sql
CREATE UNIQUE INDEX UK_DistributionOrder_IdempotencyKey
ON CoreCmsDistributionOrder (idempotencyKey);
```

### 旧字段兼容

| 旧字段 | 处理 |
|--------|------|
| `isSettlement` | **保留**，通过映射兼容：0=未结算→对应 status=0/1，1=已结算→对应 status=2，2=已取消→对应 status=3 |
| `level` | **保留**，但强制为 1（一级返佣） |
| `amount` | **保留**，含义不变，等于 frozenAmount + availableAmount + refundAmount |

---

## 四、FinishOrder() 改造

### 4.1 当前实现（问题）

```csharp
// CoreCmsDistributionOrderServices.cs:313-345
public async Task<WebApiCallBack> FinishOrder(string orderId)
{
    // 1. 查询订单（无事务）
    var order = await _orderServices.QueryByClauseAsync(...);

    // 2. 查询未结算佣金（无事务）
    var list = await _dal.QueryListByClauseAsync(p => p.isSettlement == 0);

    // 3. 循环转入余额（Change 无事务无幂等）
    foreach (var item in list)
    {
        var result = await _balanceServices.Change(...);
        if (!result.status) { /* 静默忽略 */ }
    }

    // 4. 批量更新状态（无事务）
    await _dal.UpdateAsync(p => new ... { isSettlement = 1 }, ...);
}
```

**问题**：
1. 步骤 3 中 Change() 失败被静默忽略，佣金标记为已结算但钱没入账
2. 步骤 3 和 4 不在同一事务，可能钱入账了但状态没更新
3. 无幂等，重复消费会重复入账
4. 直接转入普通余额，无冻结期

### 4.2 新实现

```csharp
public async Task<WebApiCallBack> FinishOrder(string orderId)
{
    var jm = new WebApiCallBack();

    // 1. 查询订单状态
    var order = await _orderServices.QueryByClauseAsync(
        p => p.orderId == orderId
          && p.status == (int)GlobalEnumVars.OrderStatus.Complete);
    if (order == null)
    {
        jm.msg = "订单查询失败";
        return jm;
    }

    // 2. 查询 Frozen 状态的佣金记录
    var list = await _dal.QueryListByClauseAsync(
        p => p.orderId == orderId
          && p.status == (int)CommissionStatus.Frozen);

    if (list == null || !list.Any())
    {
        jm.status = true;
        jm.msg = "无待结算佣金";
        return jm;
    }

    // 3. 逐笔处理（每笔独立事务）
    foreach (var item in list)
    {
        await SettleSingleCommission(item);
    }

    jm.status = true;
    return jm;
}

private async Task SettleSingleCommission(CoreCmsDistributionOrder commission)
{
    var idempotencyKey = $"CommissionSettle:{commission.orderId}:{commission.userId}";

    // 使用事务
    _unitOfWork.BeginTran();
    try
    {
        // 条件更新：Frozen → Available（幂等）
        var affected = await _dal.DbClient.Updateable<CoreCmsDistributionOrder>()
            .SetColumns(it => new CoreCmsDistributionOrder
            {
                status = (int)CommissionStatus.Available,
                availableAmount = it.frozenAmount,
                settledTime = DateTime.Now,
                updateTime = DateTime.Now
            })
            .Where(it => it.id == commission.id)
            .Where(it => it.status == (int)CommissionStatus.Frozen)  // 状态守卫
            .ExecuteCommandAsync();

        if (affected == 0)
        {
            // 已被其他进程处理，幂等返回
            _unitOfWork.RollbackTran();
            return;
        }

        // 佣金解冻：commissionFrozen → commissionAvailable
        // （含负债抵扣逻辑，见文档 03）
        var changeResult = await _balanceServices.Change(
            commission.userId,
            (int)UserBalanceSourceTypes.CommissionUnfreeze,
            commission.frozenAmount,
            commission.orderId,
            accountType: AccountType.CommissionFrozen,  // 从冻结扣减
            idempotencyKey: idempotencyKey);

        if (!changeResult.status)
        {
            _unitOfWork.RollbackTran();
            NLogUtil.WriteFileLog(NLog.LogLevel.Error, LogType.Other,
                "佣金结算", $"佣金结算失败: {idempotencyKey}", null);
            return;
        }

        _unitOfWork.CommitTran();
    }
    catch (Exception ex)
    {
        _unitOfWork.RollbackTran();
        NLogUtil.WriteFileLog(NLog.LogLevel.Error, LogType.Other,
            "佣金结算", $"佣金结算异常: {idempotencyKey}", ex);
        throw;
    }
}
```

### 4.3 关键改变

| 维度 | 旧实现 | 新实现 |
|------|--------|--------|
| 事务 | 无 | 每笔佣金独立事务 |
| 幂等 | 无 | 状态条件更新 + 幂等键 |
| 失败处理 | 静默忽略 | 记录日志 + 回滚 |
| 目标账户 | 普通余额 | 佣金可提现余额 |
| 冻结期 | 无 | Frozen → Available（需售后保护期） |
| 重复执行 | 会重复加钱 | 状态守卫，affected=0 则跳过 |

---

## 五、佣金创建时机改造

### 5.1 当前：支付成功时创建

`OrderAgentOrDistributionSubscribe` 在支付成功时调用 `AddData()`，创建 `isSettlement=0` 的佣金记录。

### 5.2 新流程

```
支付成功
  → 创建佣金记录，status = Frozen
  → commissionFrozen 增加
  → 记录推荐关系快照

订单完成（确认收货）
  → 若 CommissionProtectionPeriodDays = 0（默认）：
      FinishOrder 立即 Frozen → Available，资金即落账（旧行为）
  → 若 CommissionProtectionPeriodDays > 0：
      FinishOrder 仅设 expectedSettleTime = 现在 + 保护期（N 天）
      记录保持 Frozen，等待定时任务

售后保护期结束（Hangfire 定时任务 CommissionSettlementJob，每小时扫描）
  → 检查：无进行中售后（BillAftersalesStatus.WaitAudit）
  → Frozen → Available
  → commissionFrozen 减少，commissionAvailable 增加
```

### 5.3 AddData() 改造

```csharp
public async Task<WebApiCallBack> AddData(CoreCmsOrder order)
{
    var jm = new WebApiCallBack();

    var user = await _userServices.QueryByClauseAsync(p => p.id == order.userId);
    if (user is not { parentId: > 0 })  // 无推荐人
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

    // 获取订单明细和商品数据
    var orderItems = await _orderItemServices.QueryListByClauseAsync(p => p.orderId == order.orderId);
    // ... 获取 goods, products, productsDistributions ...

    // 只计算一级佣金（不再递归）
    var commissionResult = CalculateCommission(
        order, orderItems, goods, products, productsDistributions, user.parentId);

    if (commissionResult.Amount <= 0)
    {
        jm.status = true;
        return jm;
    }

    // 创建佣金记录（Frozen 状态）
    var iData = new CoreCmsDistributionOrder();
    iData.userId = user.parentId;           // 推荐人
    iData.buyUserId = order.userId;         // 购买人
    iData.orderId = order.orderId;
    iData.amount = commissionResult.Amount;
    iData.frozenAmount = commissionResult.Amount;
    iData.level = 1;                        // 强制一级
    iData.status = (int)CommissionStatus.Frozen;
    iData.frozenTime = DateTime.Now;
    iData.referrerUserId = user.parentId;
    iData.commissionRateSnapshot = commissionResult.Rate;
    iData.commissionTypeSnapshot = commissionResult.Type;
    iData.ruleSnapshot = JsonConvert.SerializeObject(commissionResult.Rule);
    iData.idempotencyKey = $"CommissionFreeze:{order.orderId}:{user.parentId}";
    iData.isDelete = false;
    iData.createTime = DateTime.Now;

    // 幂等检查
    var existing = await _dal.QueryByClauseAsync(
        p => p.idempotencyKey == iData.idempotencyKey);
    if (existing != null)
    {
        jm.status = true;
        return jm;  // 幂等返回
    }

    await _dal.InsertAsync(iData);

    // 冻结用户佣金
    await _balanceServices.Change(
        user.parentId,
        (int)UserBalanceSourceTypes.CommissionFreeze,
        commissionResult.Amount,
        order.orderId,
        accountType: AccountType.CommissionFrozen,
        idempotencyKey: iData.idempotencyKey);

    jm.status = true;
    return jm;
}
```

---

## 六、佣金取消与追回

### 6.1 订单取消时

```csharp
public async Task<WebApiCallBack> CancelCommission(string orderId)
{
    // 查询该订单所有 Frozen 状态的佣金
    var frozenList = await _dal.QueryListByClauseAsync(
        p => p.orderId == orderId
          && p.status == (int)CommissionStatus.Frozen);

    foreach (var item in frozenList)
    {
        // 条件更新：Frozen → Cancelled
        var affected = await _dal.DbClient.Updateable<CoreCmsDistributionOrder>()
            .SetColumns(it => new CoreCmsDistributionOrder
            {
                status = (int)CommissionStatus.Cancelled,
                cancelledTime = DateTime.Now,
                updateTime = DateTime.Now
            })
            .Where(it => it.id == item.id)
            .Where(it => it.status == (int)CommissionStatus.Frozen)
            .ExecuteCommandAsync();

        if (affected > 0)
        {
            // 扣减冻结佣金
            await _balanceServices.Change(
                item.userId,
                (int)UserBalanceSourceTypes.CommissionCancel,
                item.frozenAmount,
                orderId,
                accountType: AccountType.CommissionFrozen,
                idempotencyKey: $"CommissionCancel:{orderId}:{item.userId}");
        }
    }
}
```

### 6.2 退款时佣金追回

```csharp
public async Task<WebApiCallBack> ClawbackCommission(string orderId, decimal refundRatio)
{
    // refundRatio = 退款金额 / 订单实付金额，用于按比例追回

    var commissions = await _dal.QueryListByClauseAsync(
        p => p.orderId == orderId
          && p.status != (int)CommissionStatus.Cancelled
          && p.status != (int)CommissionStatus.ClawedBack);

    foreach (var item in commissions)
    {
        var clawbackAmount = Math.Round(item.amount * refundRatio, 2);

        if (item.status == (int)CommissionStatus.Frozen)
        {
            // 冻结中：直接取消
            await CancelSingleCommission(item, clawbackAmount);
        }
        else if (item.status == (int)CommissionStatus.Available)
        {
            // 已解冻：追回
            await ClawbackSingleCommission(item, clawbackAmount);
        }
    }
}
```

---

## 七、Hangfire 定时结算任务

```csharp
public class CommissionSettlementJob
{
    /// <summary>
    /// 每小时执行：扫描到期佣金，Frozen → Available
    /// </summary>
    public async Task Execute()
    {
        // 实现：调用 CoreCmsDistributionOrderServices.SettleDueCommissions()
        // 该方法内部完成扫描 + 售后守卫 + SettleSingleCommission 逐笔结算。
        // 幂等：SettleSingleCommission 内部状态守卫 + 幂等键，重复调度不会重复入账。
    }
}
```

**实现要点（已落地）**：
- `CommissionSettlementJob` 位于 `CoreCms.Net.Task`，在 `HangfireDispose.HangfireService()` 中注册为每小时的 RecurringJob（cron `0 0 0/1 * * ?`）。
- 新增 `ICoreCmsDistributionOrderServices.SettleDueCommissions()`：扫描 `status=Frozen && expectedSettleTime != null && expectedSettleTime <= now && !isDelete`，对每笔查询该 `orderId` 是否存在 `BillAftersalesStatus.WaitAudit` 售后，有则跳过，无则调 `SettleSingleCommission`。
- **保护期门控**：`Distribution:CommissionProtectionPeriodDays`（默认 `"0"`=立刻结算，旧行为不变）。`>0` 时 `FinishOrder` 不再立即结算，而是设 `expectedSettleTime = Now + N天`，留待本定时任务到期结算。
- 幂等保证：`SettleSingleCommission` 内部状态守卫，重复执行不会重复入账。
