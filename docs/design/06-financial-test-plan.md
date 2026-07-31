# 06 财务测试计划

## 一、测试分层

| 层级 | 测试类型 | 目的 | 依赖 |
|------|---------|------|-------|
| L1 | 单元测试 | 验证单个方法的逻辑正确性 | 无外部依赖（Mock） |
| L2 | 集成测试 | 验证数据库事务和原子更新 | Docker MySQL + Redis |
| L3 | 并发测试 | 验证并发安全和幂等 | 多线程 + Docker MySQL |
| L4 | 端到端测试 | 验证完整业务流程 | 完整运行环境 |

---

## 二、L1 单元测试

### 2.1 Change() 方法测试

**测试类**：`CoreCmsUserBalanceServicesTests`

| # | 测试场景 | 输入 | 预期结果 |
|---|---------|------|---------|
| 1 | 正常增加余额 | userId=1, type=Recharge, money=100 | 余额增加100，流水记录正确 |
| 2 | 正常扣减余额 | userId=1, type=Pay, money=50 | 余额减少50，流水记录正确 |
| 3 | 余额不足扣减 | userId=1, type=Pay, money=999 | 返回错误 code=11007 |
| 4 | 金额为0 | userId=1, type=Recharge, money=0 | 不产生流水，直接返回成功 |
| 5 | 提现含手续费 | userId=1, type=Tocash, money=100, cateMoney=1 | 扣减101，流水记录手续费 |
| 6 | 幂等键重复 | 相同 idempotencyKey 调用两次 | 第二次返回第一次的结果，不重复加钱 |
| 7 | 幂等键为空 | idempotencyKey=null | 不做幂等检查，正常执行 |
| 8 | 佣金冻结 | accountType=CommissionFrozen, money=50 | commissionFrozen 增加50 |
| 9 | 佣金解冻 | accountType=CommissionFrozen, money=-50; accountType=CommissionAvailable, money=50 | commissionFrozen 减少50，commissionAvailable 增加50 |
| 10 | 佣金负债记录 | accountType=CommissionDebt, money=30 | commissionDebt 增加30 |

### 2.2 佣金计算测试

**测试类**：`CommissionCalculationTests`

| # | 测试场景 | 输入 | 预期结果 |
|---|---------|------|---------|
| 1 | 固定金额佣金 | type=Fixed, discount=5.00, nums=2 | 佣金 = 5.00 × 2 = 10.00 |
| 2 | 百分比佣金 | type=Percent, discount=10, amount=200, nums=1 | 佣金 = 200 × 10% = 20.00 |
| 3 | 优惠后金额计算 | amount=100, promotionAmount=20, type=Percent, discount=10 | 计算基数 = 80 |
| 4 | 优惠后金额为负 | amount=10, promotionAmount=20 | 基数 = 0 |
| 5 | 无推荐人 | user.parentId = 0 | 不产生佣金 |
| 6 | 自购不返佣 | user.parentId = user.id | 不产生佣金 |
| 7 | 非分销商 | 推荐人非分销商 | 不产生佣金 |

---

## 三、L2 集成测试

### 3.1 事务完整性测试

**测试类**：`BalanceTransactionIntegrationTests`

| # | 测试场景 | 操作 | 验证 |
|---|---------|------|------|
| 1 | 流水与余额原子一致 | Change(userId, Recharge, 100) | 流水 money=100 且 User.balance = 旧值+100 |
| 2 | 事务回滚验证 | Change 中间抛异常 | 流水不存在，余额不变 |
| 3 | 原子更新验证 | 并发两个 Change（不同 type） | 余额 = 旧值 + delta1 + delta2 |
| 4 | 余额不足原子拒绝 | balance=50, Pay=100 | 流水不存在，余额仍为50 |
| 5 | 佣金冻结+解冻完整流程 | 冻结50 → 解冻50 | commissionFrozen 最终=0，commissionAvailable 增加50 |
| 6 | 佣金冻结+取消 | 冻结50 → 取消50 | commissionFrozen 最终=0，流水两笔 |

### 3.2 佣金完整流程测试

**测试类**：`CommissionFlowIntegrationTests`

| # | 测试场景 | 步骤 | 验证 |
|---|---------|------|------|
| 1 | 正常佣金全流程 | 创建订单 → 支付(冻结佣金) → 收货 → 保护期结束(解冻) → 提现 | 佣金状态：Pending→Frozen→Available，提现成功 |
| 2 | 订单取消-冻结中 | 创建订单 → 支付(冻结佣金) → 取消订单 | 佣金状态：Frozen→Cancelled，冻结金额释放 |
| 3 | 整单退款-已解冻 | 支付 → 收货 → 解冻 → 整单退款 | 佣金追回，commissionAvailable 减少 |
| 4 | 部分退款-佣金按比例 | 支付 → 收货 → 解冻 → 退50% | 佣金按50%追回 |
| 5 | 退款-可提现不足 | 解冻佣金10 → 提现8 → 退款需追回10 | commissionAvailable=0，commissionDebt=8 |
| 6 | 负债抵扣 | 有负债8 → 新佣金解冻10 | commissionDebt=0，commissionAvailable=2 |

---

## 四、L3 并发测试

### 4.1 余额并发测试

**测试类**：`BalanceConcurrencyTests`

| # | 测试场景 | 并发数 | 操作 | 验证 |
|---|---------|------|------|------|
| 1 | 并发充值 | 10 | 每个线程 Change(Recharge, 100) | 余额 = 旧值 + 1000，流水10笔 |
| 2 | 并发扣减 | 10 | balance=1000，每个线程 Change(Pay, 50) | 余额 = 500，流水10笔 |
| 3 | 混合并发 | 20 | 10充值+10扣减，各100 | 余额 = 旧值，流水20笔 |
| 4 | 余额不足竞争 | 5 | balance=100，5个线程各 Pay=50 | 只有2个成功，3个返回余额不足 |
| 5 | 幂等键并发 | 10 | 相同 idempotencyKey，10个线程同时调用 | 只入账1次，其余幂等返回 |

**实现方式**：

```csharp
[Fact]
public async Task ConcurrentDeposit_ShouldBeAtomic()
{
    const int threadCount = 10;
    const decimal amount = 100m;
    var userId = await CreateTestUser(initialBalance: 0);

    var tasks = Enumerable.Range(0, threadCount)
        .Select(i => _balanceService.Change(
            userId, Recharge, amount,
            idempotencyKey: $"TestDeposit:{i}"))  // 每个线程不同幂等键
        .ToArray();

    await Task.WhenAll(tasks);

    var user = await _userService.QueryByIdAsync(userId);
    Assert.Equal(threadCount * amount, user.balance);

    var flows = await _balanceService.QueryListByClauseAsync(
        p => p.userId == userId && p.type == Recharge);
    Assert.Equal(threadCount, flows.Count);
}
```

### 4.2 佣金结算并发测试

| # | 测试场景 | 并发数 | 操作 | 验证 |
|---|---------|------|------|------|
| 1 | FinishOrder 幂等 | 5 | 相同 orderId 同时触发 FinishOrder | 佣金只入账1次 |
| 2 | 冻结与取消竞争 | 2 | 1个冻结+1个取消同时执行 | 最终状态一致，金额守恒 |
| 3 | 解冻与追回竞争 | 2 | 1个解冻+1个退款同时执行 | 最终状态一致，不超发不丢失 |

### 4.3 拼团并发测试

| # | 测试场景 | 并发数 | 操作 | 验证 |
|---|---------|------|------|------|
| 1 | 最后一名额竞争 | 5 | 拼团差1人，5人同时支付 | 只有1人成功，其余4人不入团 |
| 2 | 成团与超时竞争 | 2 | 1个成团+1个超时关闭 | 最终状态一致 |
| 3 | 重复支付回调 | 3 | 同一笔支付3次回调 | 只处理1次 |
| 4 | 重复退款回调 | 3 | 同一笔退款3次回调 | 只退款1次 |

---

## 五、L4 端到端测试

### 5.1 核心业务流程

| # | 测试场景 | 步骤 | 验证 |
|---|---------|------|------|
| 1 | 普通购买+佣金 | 用户B通过推荐链接注册 → 购买商品 → 支付 → 发货 → 收货 → 佣金解冻 | 推荐人A获得佣金 |
| 2 | 拼团+佣金 | 用户B推荐用户C → C参加拼团 → 成团 → 发货 → 收货 → 佣金解冻 | 推荐人B获得佣金 |
| 3 | 退款+佣金追回 | 佣金已解冻 → 用户申请售后 → 退款成功 → 佣金追回 | 佣金状态=ClawedBack |
| 4 | 提现+负债 | 佣金解冻 → 提现 → 退款 → 佣金追回不足 → 负债 | commissionDebt > 0 |
| 5 | 负债抵扣 | 有负债 → 新佣金解冻 → 负债自动抵扣 | 佣金优先还债 |

### 5.2 异常场景

| # | 测试场景 | 操作 | 预期 |
|---|---------|------|------|
| 1 | 佣金结算时数据库连接断开 | FinishOrder 执行中断开 MySQL | 事务回滚，佣金不入账 |
| 2 | RedisMQ 重复消费 | 同一订单消息被消费2次 | 幂等返回，佣金不重复入账 |
| 3 | 提现审核重复操作 | 两个管理员同时审核 | 只有一个成功，另一个返回状态已变更 |
| 4 | 并发提现 | 同一用户同时发起2笔提现 | 可提现余额够则都成功，不够则先到先得 |

---

## 六、测试数据准备

### 6.1 集成测试基础设施

```csharp
public class FinancialTestFixture : IAsyncLifetime
{
    private DockerClient _docker;
    private string _mysqlContainerId;
    private string _redisContainerId;

    public string ConnectionString { get; private set; }
    public string RedisConnectionString { get; private set; }

    public async Task InitializeAsync()
    {
        // 启动 Docker MySQL 和 Redis 测试实例
        // 创建独立测试数据库
        // 执行迁移脚本
    }

    public async Task DisposeAsync()
    {
        // 停止并删除 Docker 容器
    }
}
```

### 6.2 测试用户创建

```csharp
protected async Task<int> CreateTestUser(
    decimal balance = 0,
    decimal commissionAvailable = 0,
    decimal commissionFrozen = 0,
    int parentId = 0)
{
    var user = new CoreCmsUser
    {
        userName = $"test_{Guid.NewGuid():N}",
        mobile = $"1{Random.Shared.Next(1000000000, 9999999999)}",
        passWord = CommonHelper.EnPassword("Test123", DateTime.Now),
        balance = balance,
        commissionAvailable = commissionAvailable,
        commissionFrozen = commissionFrozen,
        parentId = parentId,
        createTime = DateTime.Now
    };
    await _userDal.InsertAsync(user);
    return user.id;
}
```

---

## 七、测试执行顺序

```
L1 单元测试（CI 快速反馈）
  → Change() 逻辑测试
  → 佣金计算测试

L2 集成测试（CI 完整验证）
  → 事务完整性测试
  → 佣金完整流程测试

L3 并发测试（本地或 Staging 执行）
  → 余额并发测试
  → 佣金结算并发测试
  → 拼团并发测试

L4 端到端测试（Staging 执行）
  → 核心业务流程
  → 异常场景
```

---

## 八、测试覆盖率目标

| 模块 | 行覆盖率目标 | 关键场景覆盖率 |
|------|------------|--------------|
| Change() | ≥ 90% | 100% |
| 佣金状态机转换 | ≥ 85% | 100%（所有合法转换） |
| FinishOrder() | ≥ 85% | 100% |
| 佣金追回逻辑 | ≥ 80% | 100%（所有追回场景） |
| 并发场景 | N/A | 100%（所有竞争条件） |
