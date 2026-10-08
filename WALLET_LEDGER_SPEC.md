# Wallet & Ledger Service

Dịch vụ backend quản lý ví: nạp, rút, chuyển tiền, hoàn tiền.

**Yêu cầu bất biến:** tiền không tự sinh ra, không tự mất, không bị trừ hai lần, kể cả khi chạy song song, retry hoặc service chết giữa chừng.

**Phạm vi:** chỉ backend + API + công cụ đối soát. Không có UI, vì giá trị nằm ở độ đúng đắn và các con số đo được.

---

## Mục lục

1. [Kiến trúc](#1-kiến-trúc)
2. [Mô hình dữ liệu](#2-mô-hình-dữ-liệu)
3. [Luồng cốt lõi: chuyển tiền](#3-luồng-cốt-lõi-chuyển-tiền)
4. [Các bài toán khó và cách giải](#4-các-bài-toán-khó-và-cách-giải)
5. [Danh sách API](#5-danh-sách-api)
6. [Lộ trình 10 tuần](#6-lộ-trình-10-tuần)
7. [Chiến lược test](#7-chiến-lược-test)
8. [Test API thủ công: Swagger UI rồi `.http`](#8-test-api-thủ-công-swagger-ui-rồi-http)
9. [Lỗi người mới hay mắc](#9-lỗi-người-mới-hay-mắc)
10. [Câu hỏi phỏng vấn](#10-câu-hỏi-phỏng-vấn)
11. [Mở rộng](#11-mở-rộng-khi-đã-xong-lõi)

---

## 1. Kiến trúc

- **ASP.NET Core Web API**, modular monolith: Accounts, Ledger, Transfers, Holds, Reconciliation, Outbox
- **PostgreSQL** là nguồn sự thật duy nhất (cần transaction, row lock, constraint trigger)
- **Redis**: cache số dư đọc nhanh (tùy chọn, làm sau)
- **RabbitMQ hoặc Kafka**: nhận sự kiện từ outbox (giai đoạn 4)
- **Worker service riêng**: outbox publisher, job hết hạn hold, job đối soát
- **Observability**: Serilog, OpenTelemetry, Prometheus + Grafana
- **Test**: xUnit, Testcontainers (Postgres thật), k6 cho load test

## 2. Mô hình dữ liệu

Quy ước: tiền lưu bằng `bigint` theo đơn vị nhỏ nhất (VND là đồng, USD là cent). Tuyệt đối không dùng `double`, hạn chế cả `decimal` trong code nghiệp vụ.

```sql
accounts (
  id uuid PK, owner_id uuid, type text,        -- USER | MERCHANT | SYSTEM
  currency char(3), status text,               -- ACTIVE | FROZEN | CLOSED
  allow_negative boolean DEFAULT false,        -- true cho tài khoản hệ thống
  created_at timestamptz
)

account_balances (                              -- bảng phi chuẩn hóa để đọc nhanh
  account_id uuid PK REFERENCES accounts,
  balance bigint NOT NULL,
  held bigint NOT NULL DEFAULT 0,               -- tiền đang bị giữ
  version bigint NOT NULL,
  CHECK (allow_negative OR balance - held >= 0) -- viết bằng trigger nếu cần tra cột bảng khác
)

transactions (
  id uuid PK, type text,                        -- TRANSFER | TOPUP | WITHDRAW | REFUND | REVERSAL
  status text,                                  -- POSTED | REVERSED
  idempotency_key text UNIQUE,
  reference text, metadata jsonb,
  reversal_of uuid NULL REFERENCES transactions,
  created_at timestamptz
)

ledger_entries (                                -- BẤT BIẾN: chỉ INSERT, không UPDATE/DELETE
  id bigserial PK,
  transaction_id uuid REFERENCES transactions,
  account_id uuid REFERENCES accounts,
  direction text,                               -- DEBIT | CREDIT
  amount bigint CHECK (amount > 0),
  balance_after bigint,
  created_at timestamptz
)

holds (
  id uuid PK, account_id uuid, amount bigint,
  status text,                                  -- ACTIVE | CAPTURED | RELEASED | EXPIRED
  expires_at timestamptz
)

idempotency_records (
  key text PK, request_hash text,
  response_status int, response_body jsonb,
  locked_at timestamptz, created_at timestamptz
)

outbox_messages (
  id uuid PK, type text, payload jsonb,
  created_at timestamptz, processed_at timestamptz NULL
)
```

**Các ràng buộc phải đặt ở tầng DB, không chỉ ở code:**

- Với mỗi `transaction_id`, tổng DEBIT = tổng CREDIT (dùng `CONSTRAINT TRIGGER ... DEFERRABLE INITIALLY DEFERRED` kiểm tra lúc commit)
- Chặn UPDATE/DELETE trên `ledger_entries` bằng trigger hoặc revoke quyền
- `idempotency_key` UNIQUE
- Tài khoản thường không được âm

## 3. Luồng cốt lõi: chuyển tiền

```
POST /transfers
Header: Idempotency-Key: <uuid>
Body: { fromAccountId, toAccountId, amount, currency, reference }
```

1. **Idempotency:** INSERT vào `idempotency_records`. Nếu key đã tồn tại thì so `request_hash`: giống thì trả lại response cũ, khác thì trả `422`. Nếu đang xử lý dở thì trả `409`.
2. `BEGIN`
3. Khóa hai tài khoản **theo thứ tự id tăng dần** để tránh deadlock:
   ```sql
   SELECT account_id, balance, held FROM account_balances
   WHERE account_id IN (@a, @b) ORDER BY account_id FOR UPDATE;
   ```
4. Kiểm tra trạng thái tài khoản, cùng tiền tệ, `balance - held >= amount`
5. Insert `transactions`, 2 dòng `ledger_entries` (DEBIT bên gửi, CREDIT bên nhận), cập nhật `account_balances`
6. Insert `outbox_messages` (`TransferCompleted`) trong cùng transaction
7. Lưu response vào `idempotency_records`
8. `COMMIT`

> Bước 3 là chỗ nhiều người làm sai. Nếu A chuyển cho B đồng thời B chuyển cho A mà khóa theo thứ tự "người gửi trước", bạn sẽ gặp deadlock. Khóa theo id tăng dần thì không bao giờ xảy ra.

Phác thảo bằng EF Core:

```csharp
await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

var ids = new[] { cmd.From, cmd.To }.OrderBy(x => x).ToArray();
var balances = await db.AccountBalances
    .FromSqlInterpolated($@"SELECT * FROM account_balances
                            WHERE account_id = ANY({ids}) ORDER BY account_id FOR UPDATE")
    .ToListAsync();

var from = balances.Single(b => b.AccountId == cmd.From);
var to   = balances.Single(b => b.AccountId == cmd.To);

if (from.Balance - from.Held < cmd.Amount) return Result.InsufficientFunds();

from.Balance -= cmd.Amount;
to.Balance   += cmd.Amount;
// thêm Transaction, 2 LedgerEntry, OutboxMessage, rồi SaveChanges + Commit
```

## 4. Các bài toán khó và cách giải

| Bài toán | Cách giải | Cách chứng minh |
|---|---|---|
| Hai request trừ cùng ví cùng lúc | `SELECT ... FOR UPDATE` theo thứ tự id | Test chạy 1.000 lần trừ song song, số dư cuối phải đúng |
| Client retry khi timeout | Idempotency key + lưu response | Gửi cùng key 20 lần song song, chỉ 1 giao dịch được ghi |
| Service chết sau commit nhưng trước khi gửi event | Outbox pattern, worker đọc `processed_at IS NULL` rồi publish, consumer phải idempotent | Kill process ngẫu nhiên giữa chừng, không mất và không trùng ảnh hưởng |
| Số dư lệch so với sổ cái | Job đối soát: `SUM(credit) - SUM(debit)` theo tài khoản so với `balance` | Cố tình sửa tay một dòng, job phải phát hiện và báo |
| Sai thì sửa thế nào | Không sửa, không xóa. Tạo giao dịch đảo (reversal) liên kết `reversal_of` | Không có UPDATE nào trên `ledger_entries` |
| Giữ tiền trước, trừ sau (thanh toán thẻ, đặt cọc) | Hold → capture hoặc release; job quét hold hết hạn | Test hold hết hạn đúng lúc capture |
| Tài khoản "nóng" (ví công ty nhận hàng nghìn giao dịch/giây) | Khóa hàng ở một dòng thành nút thắt. Giải pháp: chia sub-account (shard) rồi gộp, hoặc ghi entry trước và tính số dư bất đồng bộ | Load test trước/sau, so sánh TPS |
| Báo cáo số dư tại một thời điểm | Truy vấn từ `ledger_entries` theo `created_at` | So khớp với snapshot cuối ngày |

## 5. Danh sách API

| Method | Endpoint | Ghi chú |
|---|---|---|
| POST | `/accounts` | Tạo tài khoản |
| GET | `/accounts/{id}` | Thông tin tài khoản |
| GET | `/accounts/{id}/balance` | Số dư |
| GET | `/accounts/{id}/entries?cursor=...` | Phân trang kiểu cursor, không dùng offset |
| POST | `/transfers` | Cần `Idempotency-Key` |
| POST | `/topups` | Cần `Idempotency-Key` |
| POST | `/withdrawals` | Cần `Idempotency-Key` |
| POST | `/transactions/{id}/reverse` | Tạo giao dịch đảo |
| POST | `/holds` | Giữ tiền |
| POST | `/holds/{id}/capture` | Trừ tiền đã giữ |
| POST | `/holds/{id}/release` | Nhả tiền đã giữ |
| GET | `/admin/reconciliation/runs` | Lịch sử đối soát |
| POST | `/admin/reconciliation/run` | Chạy đối soát |
| POST | `/webhooks/payment-provider` | Nhận kết quả nạp tiền, verify chữ ký HMAC, chống replay |

## 6. Lộ trình 10 tuần

Mỗi giai đoạn có tiêu chí hoàn thành.

**Tuần 1-2: Lõi sổ cái**
- Schema, migration, tạo tài khoản, nạp tiền (từ tài khoản hệ thống), chuyển tiền
- *Xong khi:* có test chứng minh tổng tiền toàn hệ thống không đổi sau mọi giao dịch nội bộ

**Tuần 3: Đồng thời và idempotency**
- Khóa theo thứ tự, idempotency key đầy đủ
- *Xong khi:* test 1.000 chuyển tiền song song giữa 10 ví, kể cả chuyển chéo, không deadlock, tổng tiền khớp

**Tuần 4: Hold, reversal, kiểm tra bất biến ở DB**
- Hold/capture/release, reversal, constraint trigger cân bằng DEBIT/CREDIT
- *Xong khi:* cố tình insert entry lệch thì DB từ chối

**Tuần 5-6: Outbox và sự kiện**
- Outbox publisher, RabbitMQ, một consumer mẫu (gửi thông báo, ghi audit)
- *Xong khi:* kill worker giữa chừng, không mất sự kiện và consumer xử lý trùng vẫn an toàn

**Tuần 7: Đối soát và nạp tiền qua webhook**
- Job đối soát, webhook nhà cung cấp (mô phỏng bằng sandbox hoặc mock), verify chữ ký, xử lý webhook gọi lặp
- *Xong khi:* gọi cùng webhook 10 lần chỉ nạp một lần

**Tuần 8: Hiệu năng**
- Load test k6, đo TPS và p95/p99, đọc `EXPLAIN ANALYZE`, tối ưu index
- Xử lý bài toán tài khoản nóng
- *Xong khi:* có biểu đồ trước/sau tối ưu và nêu rõ nút thắt nào, sửa thế nào

**Tuần 9: Vận hành**
- Auth (JWT + API key cho dịch vụ gọi vào), rate limit, audit log, metrics, dashboard, health check
- Docker Compose, GitHub Actions chạy test với Testcontainers

**Tuần 10: Hoàn thiện hồ sơ**
- README có sơ đồ kiến trúc, ERD, sơ đồ tuần tự luồng chuyển tiền
- File `DESIGN.md` ghi các quyết định và đánh đổi (vì sao chọn pessimistic lock, vì sao outbox, các phương án đã loại)
- Thư mục `http/` có đủ request mẫu cho mọi endpoint (xem mục 8)

## 7. Chiến lược test

Có hai lớp kiểm thử với hai mục đích khác nhau:

| Lớp | Mục đích | Công cụ |
|---|---|---|
| **Test tự động** | *Chứng minh* hệ thống đúng | xUnit, Testcontainers, FsCheck, k6 |
| **Test API thủ công** | *Thử tay, demo, tài liệu sống* | Swagger UI, rồi file `.http` |

Test thủ công không chứng minh được tính đúng đắn, vì nó gửi request lần lượt chứ không tạo được 1.000 request song song có kiểm soát và không tự kiểm tra bất biến.

### Test tự động (sản phẩm chính)

| Cần chứng minh | Công cụ |
|---|---|
| 1.000 chuyển tiền song song, không deadlock, tổng tiền khớp | xUnit + Testcontainers (Postgres thật) + `Task.WhenAll` |
| Cùng idempotency key gửi 20 lần song song chỉ ghi 1 giao dịch | xUnit + Testcontainers |
| Bất biến với chuỗi giao dịch ngẫu nhiên ("tổng debit = tổng credit", "không số dư âm") | FsCheck |
| Lỗi giữa chừng: ném exception ở từng bước, không có trạng thái nửa vời | xUnit + Testcontainers |
| Kill worker với outbox | xUnit + Testcontainers, hoặc script kill process |
| TPS, p95/p99, tài khoản nóng | k6 |
| Số dư lệch so với sổ cái | Job đối soát |

Lưu ý: dùng Testcontainers chạy Postgres thật, **không** dùng in-memory của EF vì nó không có row lock.

## 8. Test API thủ công: Swagger UI rồi `.http`

### Giai đoạn 1: Swagger UI (trong lúc phát triển)

Dùng ngay từ tuần 1 để thử tay, không cần cài thêm công cụ.

- Với .NET 9 trở lên: dùng `Microsoft.AspNetCore.OpenApi` kèm `Swashbuckle.AspNetCore` (hoặc `Scalar.AspNetCore` nếu thích giao diện hiện đại hơn).
- Chỉ bật ở môi trường Development:

```csharp
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();   // mở tại /swagger
}
```

**Mẹo quan trọng:** để Swagger UI cho nhập header `Idempotency-Key`, hãy khai báo nó là tham số của endpoint (hoặc dùng `IOperationFilter`) cho các endpoint `POST /transfers`, `/topups`, `/withdrawals`. Nếu quên, bạn sẽ không gửi được header này từ giao diện.

### Giai đoạn 2: file `.http` (khi chuẩn bị đẩy lên Git)

Chuyển các request đã thử ở Swagger thành file `.http` và commit vào repo. Ai clone về cũng chạy được ngay bằng Visual Studio, Rider hoặc VS Code (extension REST Client), không cần cài Postman hay import collection.

**Cấu trúc thư mục gợi ý:**

```
wallet-ledger/
├── src/
├── tests/
├── http/
│   ├── http-client.env.json     # biến môi trường (dev, staging)
│   ├── 01-accounts.http
│   ├── 02-topups-transfers.http
│   ├── 03-holds.http
│   ├── 04-reversals.http
│   ├── 05-reconciliation.http
│   └── 06-webhooks.http
├── README.md
└── DESIGN.md
```

**`http/http-client.env.json`**

```json
{
  "dev": {
    "baseUrl": "https://localhost:5001",
    "systemAccountId": "00000000-0000-0000-0000-000000000001"
  }
}
```

**`http/02-topups-transfers.http`** (ví dụ một luồng đầy đủ)

```http
### 1. Tạo tài khoản Alice
# @name createAlice
POST {{baseUrl}}/accounts
Content-Type: application/json

{
  "ownerId": "11111111-1111-1111-1111-111111111111",
  "type": "USER",
  "currency": "VND"
}

### 2. Tạo tài khoản Bob
# @name createBob
POST {{baseUrl}}/accounts
Content-Type: application/json

{
  "ownerId": "22222222-2222-2222-2222-222222222222",
  "type": "USER",
  "currency": "VND"
}

### 3. Nạp 1.000.000 VND cho Alice
POST {{baseUrl}}/topups
Content-Type: application/json
Idempotency-Key: {{$guid}}

{
  "accountId": "{{createAlice.response.body.$.id}}",
  "amount": 1000000,
  "currency": "VND",
  "reference": "topup-demo-001"
}

### 4. Alice chuyển 250.000 VND cho Bob
# @name transfer1
POST {{baseUrl}}/transfers
Content-Type: application/json
Idempotency-Key: 3f1c2a9e-7a52-4c8e-9a6b-0d2f5b7c1e11

{
  "fromAccountId": "{{createAlice.response.body.$.id}}",
  "toAccountId": "{{createBob.response.body.$.id}}",
  "amount": 250000,
  "currency": "VND",
  "reference": "transfer-demo-001"
}

### 5. Gửi lại đúng request ở bước 4 (cùng Idempotency-Key)
### Kỳ vọng: nhận lại response cũ, KHÔNG tạo giao dịch mới
POST {{baseUrl}}/transfers
Content-Type: application/json
Idempotency-Key: 3f1c2a9e-7a52-4c8e-9a6b-0d2f5b7c1e11

{
  "fromAccountId": "{{createAlice.response.body.$.id}}",
  "toAccountId": "{{createBob.response.body.$.id}}",
  "amount": 250000,
  "currency": "VND",
  "reference": "transfer-demo-001"
}

### 6. Cùng Idempotency-Key nhưng đổi amount
### Kỳ vọng: 422 Unprocessable Entity
POST {{baseUrl}}/transfers
Content-Type: application/json
Idempotency-Key: 3f1c2a9e-7a52-4c8e-9a6b-0d2f5b7c1e11

{
  "fromAccountId": "{{createAlice.response.body.$.id}}",
  "toAccountId": "{{createBob.response.body.$.id}}",
  "amount": 999999,
  "currency": "VND",
  "reference": "transfer-demo-001"
}

### 7. Kiểm tra số dư
GET {{baseUrl}}/accounts/{{createAlice.response.body.$.id}}/balance

### 8. Xem sổ cái của Alice (cursor pagination)
GET {{baseUrl}}/accounts/{{createAlice.response.body.$.id}}/entries?limit=20

### 9. Hoàn tác giao dịch
POST {{baseUrl}}/transactions/{{transfer1.response.body.$.transactionId}}/reverse
Content-Type: application/json
Idempotency-Key: {{$guid}}

{
  "reason": "demo reversal"
}
```

**Lưu ý về cú pháp giữa các công cụ:**

| Điểm | Visual Studio / VS Code REST Client | Rider |
|---|---|---|
| Sinh GUID | `{{$guid}}` | `{{$uuid}}` |
| Lấy giá trị từ response trước | `{{tên.response.body.$.id}}` | `{{tên.response.body.$.id}}` |
| File biến môi trường | `http-client.env.json` (VS Code REST Client dùng `settings.json`) | `http-client.env.json` |

Hãy chọn một công cụ làm chuẩn và ghi rõ trong README.

**Những thứ `.http` không làm được (phải dùng test tự động):**

- Gửi 20 request song song cùng idempotency key
- Chạy 1.000 chuyển tiền song song và kiểm tra tổng tiền
- Tính chữ ký HMAC cho `POST /webhooks/payment-provider` một cách tiện lợi. Cách xử lý: dùng một endpoint hoặc script dev-only để sinh chữ ký, hoặc để test webhook nằm trong xUnit.

### Gợi ý đoạn README cho người clone repo

```markdown
## Chạy thử API

1. `docker compose up -d`     # Postgres, RabbitMQ
2. `dotnet run --project src/Wallet.Api`
3. Mở thư mục `http/` bằng Visual Studio / Rider / VS Code (REST Client),
   chọn môi trường `dev`, chạy lần lượt các request trong `02-topups-transfers.http`.
4. Hoặc mở Swagger UI tại https://localhost:5001/swagger
```

## 9. Lỗi người mới hay mắc

- Dùng `double`/`float` cho tiền
- Đọc số dư, kiểm tra, rồi ghi ở hai câu lệnh không khóa (race condition kinh điển)
- `UPDATE balance = balance - x` mà không kiểm tra điều kiện, dẫn đến số dư âm
- Cho phép sửa hoặc xóa entry cũ
- Gửi message ngay trong code nghiệp vụ thay vì qua outbox
- Idempotency chỉ kiểm tra bằng `if exists` trong code (hai request đến cùng lúc vẫn lọt), phải dựa vào unique constraint
- Test bằng DB giả

## 10. Câu hỏi phỏng vấn

Dự án này giúp bạn trả lời:

- Vì sao dùng `bigint` thay vì `decimal`?
- Hai giao dịch chuyển chéo nhau gây deadlock thế nào, bạn tránh ra sao?
- Idempotency key được lưu ở đâu, xử lý request đến cùng lúc thế nào?
- Outbox giải quyết vấn đề gì mà "commit rồi publish" không giải quyết được?
- Mức cô lập nào bạn dùng, vì sao Read Committed + khóa hàng là đủ?
- Làm sao biết số dư trong DB đang đúng?
- Hệ thống mở rộng thế nào khi một ví nhận 5.000 giao dịch/giây?

## 11. Mở rộng khi đã xong lõi

- Đa tiền tệ và tỷ giá, phí giao dịch, tài khoản doanh thu
- Hạn mức giao dịch, phát hiện gian lận đơn giản (rule engine)
- Tách schema theo tenant để làm ví cho nhiều đối tác
- Event sourcing thật sự cho account
- Tách Ledger thành service riêng, giao tiếp qua gRPC

---

> **Lưu ý:** dự án này nhỏ về số màn hình nhưng rất dễ làm sai mà không biết. Hãy coi các test song song và job đối soát là sản phẩm chính, vì chúng chứng minh bạn làm đúng.
