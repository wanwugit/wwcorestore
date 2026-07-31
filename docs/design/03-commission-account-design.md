# 03 佣金账户设计

## 一、方案对比：新增表 vs 扩展 CoreCmsUser

### 方案 A：直接扩展 CoreCmsUser

在 `CoreCmsUser` 表新增 3 个字段：

| 字段 | 类型 | 说明 |
|------|------|------|
| `commissionAvailable` | decimal(18,2) | 佣金可提现余额 |
| `commissionFrozen` | decimal(18,2) | 佣金冻结余额 |
| `commissionDebt` | decimal(18,2) | 佣金负债 |

### 方案 B：新增 CoreCmsUserAccount 表

```sql
CREATE TABLE CoreCmsUserAccount (
    id          INT PRIMARY KEY AUTO_INCREMENT,
    userId      INT NOT NULL,
    accountType INT NOT NULL,  -- 0=Balance 1=CommissionAvailable 2=CommissionFrozen 3=CommissionDebt
    balance     DECIMAL(18,2) NOT NULL DEFAULT 0,
    version     INT NOT NULL DEFAULT 0,  -- 乐观锁版本号
    createTime  DATETIME NOT NULL,
    updateTime  DATETIME NULL,
    UNIQUE KEY UK_UserAccount_UserId_Type (userId, accountType)
);
```

### 对比

| 维度 | 方案 A 扩展 User | 方案 B 新增 Account |
|------|------------------|---------------------|
| **改动范围** | 小，1 表 3 字段 | 中，新增 1 表 + 新增 Repository/Service |
| **原子更新** | ✅ 简单，直接 UPDATE 同表 | ❌ 复杂，余额在另一张表 |
| **事务复杂度** | 低，同一行数据 | 中，跨表事务 |
| **查询性能** | ✅ 无 JOIN | 需 JOIN 或两次查询 |
| **扩展性** | 差，再增账户类型需加列 | ✅ 好，新增行即可 |
| **现有代码兼容** | ✅ 高，balance 字段保留不变 | 中，需新增读写逻辑 |
| **乐观锁** | 可选 | 内置 version |
| **迁移复杂度** | 低，ALTER TABLE | 中，需初始化数据 |

### 决策：**方案 A — 直接扩展 CoreCmsUser**

理由：
1. 本项目定位为单商户自营商城，账户类型固定为 4 种（Balance + 3 种佣金），不需要方案 B 的扩展性
2. 方案 A 的原子更新最简单，一行 UPDATE 搞定，跨表事务是性能和可靠性的隐患
3. 现有代码高度依赖 `CoreCmsUser.balance`，扩展字段比替换表更容易兼容
4. 乐观锁不需要：原子更新 `SET x = x + @delta WHERE x + @delta >= 0` 本身就是行锁保护

---

## 二、CoreCmsUser 新增字段

```sql
ALTER TABLE CoreCmsUser
    ADD COLUMN commissionAvailable DECIMAL(18,2) NOT NULL DEFAULT 0 COMMENT '佣金可提现余额',
    ADD COLUMN commissionFrozen    DECIMAL(18,2) NOT NULL DEFAULT 0 COMMENT '佣金冻结余额',
    ADD COLUMN commissionDebt      DECIMAL(18,2) NOT NULL DEFAULT 0 COMMENT '佣金负债';
```

### 实体类新增

```csharp
// CoreCmsUser.cs 或 CoreCmsUserPartial.cs
[Display(Name = "佣金可提现余额")]
[SugarColumn(ColumnDescription = "佣金可提现余额")]
public decimal commissionAvailable { get; set; }

[Display(Name = "佣金冻结余额")]
[SugarColumn(ColumnDescription = "佣金冻结余额")]
public decimal commissionFrozen { get; set; }

[Display(Name = "佣金负债")]
[SugarColumn(ColumnDescription = "佣金负债")]
public decimal commissionDebt { get; set; }
```

---

## 三、四种资金的关系

```
CoreCmsUser 资金视图
┌─────────────────────────────────────────────┐
│ balance (普通余额)                            │
│   来源：充值、退款、后台调整、奖励              │
│   用途：余额支付                              │
│   提现：不允许（仅佣金可提现）                  │
├─────────────────────────────────────────────┤
│ commissionFrozen (佣金冻结)                    │
│   来源：订单支付成功时冻结                      │
│   转化：售后保护期结束后 → commissionAvailable  │
│   取消：退款时直接扣减                         │
├─────────────────────────────────────────────┤
│ commissionAvailable (佣金可提现)               │
│   来源：commissionFrozen 解冻                  │
│   用途：提现                                   │
│   追回：退款时扣减，不足部分记入 commissionDebt │
├─────────────────────────────────────────────┤
│ commissionDebt (佣金负债)                      │
│   来源：佣金已提现后退款，可提现余额不足追回     │
│   偿还：新佣金入账时优先抵扣                    │
└─────────────────────────────────────────────┘
```

### 核心不变量

```
对任意用户：
  commissionFrozen >= 0
  commissionAvailable >= 0
  commissionDebt >= 0
  balance >= 0

  历史佣金总额 = Σ(Distribution 流水的正数金额)
  已提现总额 = Σ(Tocash 流水的绝对值)
  已追回总额 = Σ(Clawback 流水的绝对值)

  不变式：
  commissionFrozen + commissionAvailable + 已提现 + 已追回 + commissionDebt
  = 历史佣金总额
```

---

## 四、佣金入账与负债抵扣流程

### 4.1 正常佣金入账（佣金解冻）

```
佣金从 Frozen → Available 时：

1. 检查 commissionDebt > 0？
   → 是：先抵扣负债
     UPDATE CoreCmsUser
     SET commissionDebt = commissionDebt - LEAST(@amount, commissionDebt),
         commissionAvailable = commissionAvailable + @amount - LEAST(@amount, commissionDebt)
     WHERE id = @userId

     记录两笔流水：
     - CommissionDebtOffset: 抵扣负债
     - CommissionUnfreeze: 佣金解冻

   → 否：直接解冻
     UPDATE CoreCmsUser
     SET commissionFrozen = commissionFrozen - @amount,
         commissionAvailable = commissionAvailable + @amount
     WHERE id = @userId
       AND commissionFrozen - @amount >= 0

     记录一笔流水：
     - CommissionUnfreeze: 佣金解冻
```

### 4.2 提现流程

```
用户申请提现 @amount：

1. 检查 commissionAvailable >= @amount + @fee
   → 否：返回余额不足

2. 原子扣减：
   UPDATE CoreCmsUser
   SET commissionAvailable = commissionAvailable - @amount - @fee
   WHERE id = @userId
     AND commissionAvailable - @amount - @fee >= 0

3. 创建提现记录（CoreCmsUserTocash）

4. 记录流水：
   - Tocash: 扣减可提现佣金
```

### 4.3 退款追回流程

```
订单退款，佣金需追回 @amount：

1. 检查佣金当前状态
   → Frozen：直接取消
     UPDATE CoreCmsUser
     SET commissionFrozen = commissionFrozen - @amount
     WHERE id = @userId
       AND commissionFrozen - @amount >= 0
     流水：CommissionCancel

   → Available 且 commissionAvailable >= @amount：直接追回
     UPDATE CoreCmsUser
     SET commissionAvailable = commissionAvailable - @amount
     WHERE id = @userId
       AND commissionAvailable - @amount >= 0
     流水：CommissionClawback

   → Available 但不足：先扣可提现，剩余记负债
     DECLARE @deductFromAvailable DECIMAL = commissionAvailable;
     DECLARE @debt DECIMAL = @amount - @deductFromAvailable;

     UPDATE CoreCmsUser
     SET commissionAvailable = 0,
         commissionDebt = commissionDebt + @debt
     WHERE id = @userId

     流水1：CommissionClawback (扣减可提现部分)
     流水2：CommissionDebtRecord (记入负债)

   → 已全部提现：全部记负债
     UPDATE CoreCmsUser
     SET commissionDebt = commissionDebt + @amount
     WHERE id = @userId

     流水：CommissionDebtRecord
```

---

## 五、对前端展示的影响

### 5.1 小程序"我的佣金"页面

| 展示项 | 数据来源 |
|--------|---------|
| 冻结中佣金 | `commissionFrozen` |
| 可提现佣金 | `commissionAvailable` |
| 已提现佣金 | `SELECT SUM(ABS(money)) FROM CoreCmsUserBalance WHERE userId=@uid AND type=Tocash` |
| 待还负债 | `commissionDebt` |
| 累计佣金 | `SELECT SUM(money) FROM CoreCmsUserBalance WHERE userId=@uid AND type=Distribution` |

### 5.2 提现按钮

可提现金额 = `commissionAvailable`

提现时检查：`commissionAvailable >= 申请金额 + 手续费`

### 5.3 兼容旧版

旧版前端只展示 `balance`，不会展示 `commissionAvailable` 等新字段。因此：
- 旧版前端在改造前仍然看到旧的余额和佣金页面
- 新版前端需要适配新字段
- 两者可以并行存在
