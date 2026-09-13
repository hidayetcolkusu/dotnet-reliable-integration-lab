# Kod rehberi — yedi oturumda güvenilir entegrasyon

Bu rehber kodu **okuma sırasıyla değil, verinin izlediği sırayla** anlatır. Her oturumda:
takip edeceğin kod yolu, çağrı zinciri, çalıştırabileceğin bir komut, beklenen çıktı ve
cevaplı iki soru var.

Önkoşul: [README'deki kurulum](../../README.md#setup) tamamlanmış olmalı
(`docker compose up -d` ve `pwsh -File scripts/init-lab.ps1`).

Bir tavsiye: her oturumda önce **komutu çalıştır**, çıktıyı gör, sonra kodu oku. Bu konudaki
kavramların çoğu soyut okunduğunda ikna edici gelmez; çalışırken bakınca apaçık olur.

---

## Oturum 1 — Kabul etmek ile yayınlamak aynı şey değil

**Takip edilecek akış:** HTTP isteği → tek SQL transaction → `ExportRequests` + `OutboxMessages`

**Çağrı zinciri:**

```text
ExportEndpoints.SubmitExportAsync          src/Integration.Api/Exports/ExportEndpoints.cs
  └─ ExportRequestParser.Parse             (elle, sınırlı, her hata için ayrı errorCode)
  └─ SubmitExportHandler.HandleAsync       src/Integration.Api/Exports/SubmitExportHandler.cs
       └─ BeginTransactionAsync
       └─ ExportRequests.Add   + SaveChanges
       └─ OutboxMessages.Add   + SaveChanges
       └─ CommitAsync
```

**Deney — broker kapalıyken kabul:**

```powershell
pwsh -File scripts/run-scenario.ps1 -Scenario broker-down
```

**Beklenen çıktı:**

```text
==> Stopping the RabbitMQ container
    [requestId=...] API answered 202 with the broker DOWN
    [requestId=...] outbox=Pending (durable, waiting for the broker)
```

**Dikkat edilecek nokta:** `Integration.Api` içinde `RabbitConnection` kaydı **yoktur**. API
broker'a hiç bağlanmaz. Bu bir optimizasyon değil, mimari bir sınırdır: broker'ın ayakta
olması, işi kabul etmenin önkoşulu değildir.

**Soru 1:** `SaveChangesAsync` iki kez çağrılıyor. Neden tek transaction sayılıyor?
**Cevap:** `SaveChangesAsync` değişiklikleri gönderir ama commit etmez. Commit'i açıkça açılan
`BeginTransactionAsync`/`CommitAsync` çifti yapar. İkisi arasında process ölürse SQL Server
transaction'ı geri alır — `RecoveryTests.ACrashBetweenTheRequestAndItsOutboxRowLeavesNoAccepted‑
WorkBehind` tam olarak bunu, gerçek bir child process'i öldürerek kanıtlar.

**Soru 2:** Aynı `requestId` ile iki kez POST edilirse ne olur?
**Cevap:** `ExportRequests.RequestId` primary key olduğu için ikincisi unique violation alır.
Handler bunu yakalar, **taze bir context** ile commit edilmiş satırı okur ve payload hash'ini
karşılaştırır: aynıysa `202` (kabul tekrarı), farklıysa `409 duplicate_conflict`. Taze context
şart, çünkü unique violation sonrası eski `DbContext` kullanılamaz durumdadır.

---

## Oturum 2 — Confirm edilmemiş publish, publish değildir

**Takip edilecek akış:** outbox lease → `mandatory` + confirm → `Published`

**Çağrı zinciri:**

```text
OutboxDispatcher.ExecuteAsync              src/Integration.Worker/Publishing/OutboxDispatcher.cs
  └─ OutboxStore.TryClaimAsync             (UPDLOCK, READPAST; PublishAttempts += 1)
  └─ ConfirmedPublisher.PublishAsync       src/Integration.Worker/Publishing/ConfirmedPublisher.cs
       └─ mandatory: true  → yönlendirilemezse basic.return
       └─ publisher confirm → ack / nack / (hiç cevap yok)
  └─ OutboxStore.MarkPublishedAsync        (AND LeaseToken = @token)
```

**Deney — confirm sonrası publisher crash:**

```powershell
dotnet test --filter "FullyQualifiedName~OutboxTests.CrashAfterConfirmRepublishesTheSameTransportMessageId"
```

**Beklenen çıktı:** test yeşil. İçeride olan şu: gerçek bir child worker, broker confirm
verdikten **sonra** ama SQL güncellenmeden önce `Environment.Exit(70)` ile ölür. Satır
`Publishing` kalır, lease dolunca yeniden claim edilir, **aynı outbox id** ile yeniden
publish edilir.

**Dikkat edilecek nokta:** Dört farklı sonuç vardır ve dördü de farklı bir `LastErrorCode`
üretir: `broker_unroutable` (return), `broker_nack`, `broker_confirm_timeout` (hiç cevap yok),
`broker_unavailable`. Dördüncüsü — **hiç cevap gelmemesi** — en kolay unutulanıdır, çünkü
sağlıklı bir broker her zaman cevap verir.

**Soru 1:** `mandatory: true` olmasaydı ne kaybederdik?
**Cevap:** Yönlendirilemeyen bir mesajı broker sessizce **atar** ve yine de ack gönderir. Yani
satır `Published` işaretlenir, mesaj ise hiç var olmamıştır. `mandatory` bunu `basic.return`'e
çevirir; `ConfirmedPublisher` return'ü mesaj id'sine göre hatırlar ve arkasından gelen ack'in
sonucu değiştirmesine izin vermez.

**Soru 2:** Yeniden publish neden yeni bir GUID değil de aynı outbox id ile yapılıyor?
**Cevap:** Broker `MessageId`'si outbox satırının id'sidir. Aynı kimlikle tekrar yayınlanınca
tüketici tarafındaki `InboxReceipts` primary key'i (`ConsumerName`, `TransportMessageId`)
kopyayı doğrudan yutar. Yeni bir id üretmek, kopyayı tespit edilemez hale getirirdi.

---

## Oturum 3 — ACK "kabul edildi" demektir, "yapıldı" değil

**Takip edilecek akış:** delivery → (receipt + job) tek transaction → ACK

**Çağrı zinciri:**

```text
InboxConsumer.HandleDeliveryAsync          src/Integration.Worker/Consuming/InboxConsumer.cs
  └─ InboxAcceptor.AcceptAsync             src/Integration.Worker/Consuming/InboxAcceptor.cs
       └─ MessageValidator.Validate        (saf: I/O yok, saat yok, her ret için reason code)
       └─ kaynak kontrolü: ExportRequests'te var mı, hash tutuyor mu
       └─ BeginTransaction → InboxReceipts.Add (+ gerekirse IntegrationJobs.Add) → Commit
  └─ BasicAckAsync                          ← commit'ten SONRA
```

**Deney — ACK kaybı ve duplicate:**

```powershell
pwsh -File scripts/run-scenario.ps1 -Scenario duplicate-delivery
```

**Beklenen çıktı:**

```text
    [requestId=...] after the normal delivery: ... receipts=1 ...
    [requestId=...] two receipts, ONE job: ... receipts=2 ...
    [requestId=...] final: ... job=Completed/attempts=1 ... applied=1
```

**Dikkat edilecek nokta:** Receipt sayısının artması **doğrudur** — o, teslimatların denetim
izidir. Artmaması gereken şey job sayısıdır.

**Soru 1:** ACK önce, veritabanı yazımı sonra olsaydı ne olurdu?
**Cevap:** İkisinin arasında process ölürse iş kalıcı olarak kaybolur: broker mesajı teslim
edilmiş sayar, veritabanında ise hiçbir iz yoktur. Sıra bu yüzden tersine çevrilemez.

**Soru 2:** Aynı `eventId` ama farklı payload gelirse hangi reason code oluşur?
**Cevap:** `source_hash_mismatch`. Sezgisel cevap olan `identity_payload_mismatch` değildir,
çünkü kaynak kontrolü (bu `eventId` bizim kabul ettiğimiz istekle aynı hash'e sahip mi) receipt
kontrolünden **önce** çalışır. `identity_payload_mismatch` dalı arkadaki ikinci savunmadır ve
normal akışta erişilemez. `InboxTests.SameEventIdWithDifferentPayloadIsQuarantinedAndLeavesThe‑
JobUntouched` bunu ve orijinal job'a dokunulmadığını birlikte doğrular.

---

## Oturum 4 — Retry bir veritabanı satırıdır

**Takip edilecek akış:** job claim → HTTP → sonuç sınıflandırma → retry planı

**Çağrı zinciri:**

```text
JobProcessor.ExecuteAsync                  src/Integration.Worker/Processing/JobProcessor.cs
  └─ JobStore.TryClaimAsync                (AttemptsStarted += 1 — claim ile AYNI transaction)
  └─ bütçe dolmuşsa → doğrudan terminal, HTTP yok
  └─ ErpClient.ApplyAsync                  (tek deneme, tek timeout, gizli retry yok)
  └─ RetryPolicy.DelayFor                  (saf; Retry-After parse + üst sınır)
  └─ JobStore.ScheduleRetryAsync           (AND LeaseToken = @token)
```

**Deney — iki 503 sonrası başarı, sonra restart:**

```powershell
dotnet test --filter "FullyQualifiedName~JobRetryTests.TransientFailuresAreRetriedUntilTheExternalSystemAccepts"
pwsh -File scripts/run-scenario.ps1 -Scenario worker-restart
```

**Beklenen çıktı:**

```text
    [requestId=...] before the kill: ... job=RetryScheduled/attempts=1 ...
    [requestId=...] the durable counter survived the kill: attempts=1
    [requestId=...] continued from attempt 1 to 3 and applied exactly once: ... applied=1
```

**Dikkat edilecek nokta:** Sayaç **claim ile aynı transaction'da** artar. Bu yüzden çağrının
ortasında ölen bir process bedava bir deneme kazanamaz — beş çökme, beş başarısızlık kadar
bütçe tüketir. `JobRetryTests.ACrashInsideAnAttemptStillConsumesThatAttempt` bunu gerçek bir
child process'i `job.after-claim` noktasında öldürerek gösterir.

**Soru 1:** Retry'ı RabbitMQ'da TTL + DLX ile yapsaydık ne kaybederdik?
**Cevap:** Deneme sayısı bir mesaj header'ında yaşardı: `SELECT` ile görülemez, kuyruk purge
edilince silinir, broker yeniden kurulunca yok olur ve iş kuralı transport topolojisine
bağlanırdı. Gerekçenin tamamı [ADR 0002](../decisions/0002-sql-retry.md)'de.

**Soru 2:** `Retry-After: 3600` gelirse worker bir saat bekler mi?
**Cevap:** Hayır. `RetryPolicy.DelayFor` planlanan gecikme ile istenen gecikmenin büyüğünü
alır, ama `MaxRetryAfterSeconds` (varsayılan 60 sn) ile sınırlar. Dış sistemin verdiği bir
değer worker'ı istediği kadar park edemez.

---

## Oturum 5 — İşlem yapıldı, cevap kayboldu

**Takip edilecek akış:** ERP `OperationKey` → kalıcı sonuç → replay

**Çağrı zinciri:**

```text
ErpClient.ApplyAsync                       src/Integration.Worker/Processing/ErpClient.cs
  └─ Idempotency-Key header = requestId
  └─ timeout → ErpResult(Retryable: true, ErrorCode: http_timeout)   ← "bilinmiyor"
ApplyExportHandler.HandleAsync             samples/FakeErp/ApplyExportHandler.cs
  └─ AppliedExports'ta OperationKey var mı → varsa ORİJİNAL makbuz, replayed: true
  └─ aynı key + farklı payload → 409 payload_conflict
```

**Deney:**

```powershell
pwsh -File scripts/run-scenario.ps1 -Scenario erp-timeout
```

**Beklenen çıktı:**

```text
    [requestId=...] retried 2 times, applied exactly once: ... job=Completed/attempts=2 ... applied=1
```

**Dikkat edilecek nokta:** `attempts=2` **ve** `applied=1` birlikte kanıttır. Tek başına
hiçbiri yeterli değildir.

**Soru 1:** Timeout neden "başarısız" sayılmıyor?
**Cevap:** Çünkü değil. Timeout, cevabın gelmediğini söyler; etkinin olup olmadığını değil.
Bu senaryoda etki **commit edilmiştir**. "Başarısız" varsayıp yeniden denemek, dış sistem
idempotent olmasaydı çift uygulamaya yol açardı.

**Soru 2:** Dış sistem idempotency key sunmuyorsa ne yapılır?
**Cevap:** Üç seçenek var, üçü de bedelli: (1) retry'dan önce mutabakat sorgusu — yarışa açık
ama pencereyi daraltır; (2) kopyayı kabul edip telafi işlemi yapmak; (3) reddedip insana
devretmek. Hangisinin doğru olduğu bizim kodumuzun değil, karşı tarafın sözleşmesinin bir
özelliğidir. Ayrıntı: [ADR 0003](../decisions/0003-external-idempotency.md).

---

## Oturum 6 — Terminal durum ve dead-letter

**Takip edilecek akış:** terminal state → dead-letter outbox → DLQ → `DeadLettered`

**Çağrı zinciri:**

```text
JobProcessor.WriteTerminalAsync            src/Integration.Worker/Processing/JobProcessor.cs
  └─ BeginTransaction
       └─ JobStore.MoveToDeadLetterPendingAsync    (AND LeaseToken = @token)
       └─ DeadLetterWriter.WriteForJobAsync        (aynı transaction)
  └─ Commit
OutboxDispatcher.CompletePublishAsync
  └─ MarkPublishedAsync → JobStore.MarkDeadLetteredAsync    ← confirm'den SONRA
```

**Deney — kalıcı 422 ve bütçe tükenmesi:**

```powershell
dotnet test --filter "FullyQualifiedName~DeadLetterTests"
pwsh -File scripts/run-scenario.ps1 -Scenario poison-message
```

**Beklenen çıktı:**

```text
    [requestId=<marker>] quarantined durably with reason 'malformed_body' (body sha256 ...)
    [requestId=<marker>] export queue drained: acked after the quarantine committed, not requeued
    [requestId=<marker>] dead-letter queue holds 1 message(s) carrying hashes and reason codes, not the raw body
```

**Dikkat edilecek nokta:** `DeadLetterPending` ve `DeadLettered` **iki ayrı durumdur**. İş,
DLQ mesajı broker tarafından confirm edilene kadar `DeadLetterPending` kalır. Aksi halde
"dead-lettered" ifadesi, var olmayabilecek bir mesaj hakkında bir iddia olurdu.

**Soru 1:** Neden broker'ın `x-dead-letter-exchange`'i kullanılmıyor?
**Cevap:** Çünkü o, transport sorusuna cevap verir ("reddedilen mesaj nereye gider?"), iş
sorusuna değil ("hangi iş öldü, neden, kaç denemeden sonra?"). Uygulama outbox'ından geçirmek,
yeniden publish ve ACK güvenilirliğini tek bir mekanizmada tutar.

**Soru 2:** DLQ mesajında ham gövde neden yok?
**Cevap:** Zehirli gövde, içeriği ve boyutu bilinmeyen **güvenilmeyen girdidir**. Onu bir
veritabanı satırına, bir log satırına ve bir broker mesajına kopyalamak, kötü niyetli veya
devasa bir payload'ın güvenle işlenmesi gereken yer sayısını üçe katlar. Hash korelasyon için,
uzunluk ise boyutu açıklamak için yeterlidir. Bedeli de açıktır ve reponun en tartışmaya açık
kararıdır: DLQ tüketicisi orijinal baytları **alamaz**.

---

## Oturum 7 — Tek bir isteği uçtan uca takip etmek

**Takip edilecek akış:** trace context → SQL sütunu → AMQP header → sonraki process

**Çağrı zinciri:**

```text
LabTelemetry.CaptureContext                src/Integration.Shared/Diagnostics/LabTelemetry.cs
  └─ SubmitExportHandler  → OutboxMessages.TraceParent   (işle AYNI transaction'da)
MessageProperties.CreatePublishProperties  → traceparent / tracestate header'ları
MessageProperties.ReadTraceContext         → InboxAcceptor → IntegrationJobs.TraceParent
LabTelemetry.StartLinkedTo                 → her aşama saklanan context'e bağlanır
```

**Deney:**

```powershell
dotnet test --filter "FullyQualifiedName~TracingTests"
```

Görsel olarak bakmak istersen:

```powershell
docker compose --profile tracing up -d
# uygulamaları --Lab:OtlpEndpoint=http://127.0.0.1:4317 ile başlat
# http://127.0.0.1:16686
```

**Dikkat edilecek nokta:** Trace context **kalıcı olarak** taşınır. Bir saat sonra, başka bir
process'te çalışan retry bile orijinal trace'e aittir. Bellekte taşınan bir context bunu
yapamaz.

**Soru 1:** Saklanan `traceparent` bozuksa ne olur?
**Cevap:** `ActivityContext.TryParse` başarısız olur, değer düşürülür ve aşama taze bir kök
span ile başlar. İş normal şekilde tamamlanır. Gözlemlenebilirlik hiçbir zaman işin
tamamlanmasının önkoşulu değildir —
`TracingTests.AMalformedTraceParentDegradesToAFreshTraceInsteadOfPoisoningTheWork`.

**Soru 2:** Elimde sadece RabbitMQ yönetim arayüzünden bir mesaj id'si var. Trace'i nasıl
bulurum?
**Cevap:** O id outbox satırının id'sidir ve `lab.transport_message_id` etiketiyle hem publish
hem inbox span'ine yazılır. Tersi de çalışır: `lab.request_id` iş kimliğinden trace'e götürür.

---

## Buradan sonra

- Durumların tamamı ve geçişleri: [state machines](../architecture/state-machines.md)
- Adlandırılmış başarısızlık pencereleri ve her birini kapatan test: [failure windows](../architecture/failure-windows.md)
- Reddedilen alternatifleriyle birlikte kararlar: [ADR'ler](../decisions)
- Gerçek komut çıktıları: [results](../../results)
