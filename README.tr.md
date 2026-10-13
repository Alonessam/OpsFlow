# OpsFlow

[![CI](https://github.com/Alonessam/OpsFlow/actions/workflows/ci.yml/badge.svg)](https://github.com/Alonessam/OpsFlow/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10-512bd4)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

[English](README.md) | **Türkçe**

**Çok kiracılı (multi-tenant) bir operasyon masası** (bilet takibi + SLA takibi + canlı panel). Amaç: bir geliştirici bir `WHERE` koşulunu unutsa bile bir şirketin verisi başka bir şirketin kullanıcısına **ulaşamasın**.

> **Bu depodaki her şey simülasyondur.** Şirketler, kişiler ve biletler kurgusal demo verisidir. Bu bir portföy projesidir, sertleştirilmiş bir ürün değildir; bkz. [Bilinen sınırlar](#bilinen-sınırlar).

**Canlı demo: <https://opsflow-lw45.onrender.com>** - giriş sayfasındaki demo hesaplarından birine tıklayın. Ücretsiz barındırmada (Render + Neon) çalışır: 15 dakika ziyaretçi olmazsa servis uyur, bu yüzden ilk istek yaklaşık bir dakika sürebilir.

![Canlı operasyon masası: KPI'lar ve canlı SLA geri sayımlı biletler](docs/screenshot-desk.jpg)

## Mühendislik öne çıkanları

- **Kiracı izolasyonunu her sorgu değil, DbContext sağlar.** `ITenantEntity` uygulayan *her* varlığa (yansıma ile bulunur) global query filter bağlanır; yeni kayıtlar çağıranın kiracısıyla otomatik damgalanır ve bir `SaveChanges` interceptor'ı kiracı sınırını aşacak her yazmayı reddeder. Servislerde elle yazılmış kiracı koşulu yoktur; biri eklenirse kaynak kodu tarayan bir test `dotnet test`'i (dolayısıyla CI'ı) kırar.
- **Güvenli kapanış (fail-closed).** Kiracı çözümlenemezse filtre `Guid.Empty` ile karşılaştırır; hiçbir satır bunu taşımaz, sonuç "her şey" değil, boş kümedir.
- **Veritabanı da hayır der.** Bilet, yorum ve denetim kayıtları kullanıcı/bilet referanslarını **bileşik `(TenantId, Id)` yabancı anahtarlarla** tutar. PostgreSQL kiracılar arası referansı kendisi reddeder (`23503`; test *hangi* kısıtın tetiklendiğini de kontrol eder), gerçek sunucuya karşı doğrulanır.
- **Kiracı yalnızca imzalı JWT'den gelir.** İstemcinin gönderdiği `X-Tenant-Id` başlığı hem okumada hem yazmada yok sayılır (test edilmiştir); çünkü başlığı herkes yazabilir.
- **İş akışı saf bir durum makinesidir.** Geçiş matrisi + koruma kuralları (atanan zorunlu, çözüm özeti zorunlu, kapalı bileti yalnızca admin açar, çalışan yalnızca *kendi* çözülmüş biletini onaylar/reddeder). Testler 11 geçerli kenarı ve rol kurallarını tek tek yazar, her durum çiftini her rol ve talep-sahibi bayrağı için kontrol eder; bir kenarı eklemek/kaldırmak bir testi kırar.
- **Duran saatli SLA motoru.** Süreler bilet başına sabitlenir; müşteri beklenirken (`PendingClient`) saat durur, devam edince süreler ötelenir; bileti yeniden açmak yeni bir çözüm penceresi başlatır. Arka plan işçisi *uyarı* (pencerenin son %20'si), *ilk yanıt ihlali* ve *çözüm ihlali* olaylarını, taramalar üst üste binse bile, **pencere başına bir kez** üretir.
- **Otomatik denetim izi.** Bir EF Core interceptor'ı, değişen her `[Audited]` bilet alanı için (eski -> yeni, kim, ne zaman) değişiklikle **aynı transaction** içinde bir satır yazar. Sistem değişiklikleri (SLA işçisi) kullanıcısız kaydedilir.
- **Canlı operasyon masası.** SignalR olayları yalnızca doğru kişilere iletir: kiracının personeli hepsini, talep sahibi yalnızca kendi biletlerini alır; SLA alarmları ve iç notlar talep sahibine asla gitmez. Kullanıcıyı pasifleştirmek açık bağlantısını anında keser.
- **İyimser eşzamanlılık.** Her biletin bir `Version` değeri vardır; eski sürümle düzenleme `409` alır, yarışan sekiz yazar tek bir kazanan üretir (PostgreSQL'de test edilmiştir). SLA taraması kendi token'ını kullanır; böylece açık bir düzenleyicinin kopyasını asla eskimiş göstermez.

## Demoyu deneyin

Demo verisi **isteğe bağlıdır** (`Seed__Demo=true`; yalnızca Development ortamında varsayılan olarak açıktır). Giriş sayfasındaki demo düğmelerinden biriyle (ya da kiracı/e-postayı elle yazarak) girin. Tüm demo hesapların **herkese açık** parolası `Demo!Passw0rd1`'dir; gerçek veri tutan bir örnekte demoyu asla açmayın.

| Kiracı | Hesaplar |
| --- | --- |
| `acme` | `admin@acme.test` (kiracı admini), `agent1@acme.test`, `agent2@acme.test`, `employee1@acme.test`, `employee2@acme.test` |
| `globex` | aynı beş hesap, `@globex.test` ile |

Denemeye değer şeyler:

1. İki tarayıcı açın: birinde `agent1@acme.test`, diğerinde `agent1@globex.test`. `employee1@acme.test` ile bilet açın; yalnızca Acme'nin masası canlanır.
2. Acme ajanı olarak bir bileti açıp boş özetle *Çöz* deneyin (reddedilir), sonra özetle deneyin.
3. Bir bileti *Pending client* durumuna alın: SLA etiketi **Paused** olur. Devam ettirin ve sürenin ötelendiğini izleyin.
4. Biletin **Timeline** sekmesine bakın: her değişiklik, kim tarafından.
5. Acme token'ı ile bir Globex bilet kimliğini API'den isteyin: `403` değil `404` alırsınız; Acme için o bilet yoktur.

## Hızlı başlangıç

### Docker (PostgreSQL + API + arayüz)

```bash
cp .env.example .env        # JWT_KEY'i 32+ rastgele karakter yapın; demo için SEED_DEMO=true
docker compose up --build
```

<http://localhost:8080> adresini açın.

### Yerel (.NET 10 SDK)

PostgreSQL ile (boş bir veritabanı oluşturun; Development, `appsettings.Development.json`'u kullanır):

```bash
dotnet run --project src/OpsFlow.API
```

Development varsayılanı `Host=localhost;Database=opsflow;Username=postgres;Password=postgres`'tir; açılışta migration'ları uygular ve demo veriyi yükler. `ConnectionStrings__Default` ile değiştirin. Windows'ta `baslat.bat` aynısını yapar ve tarayıcıyı açar. (Boş bir veritabanına ilk açılışta EF, geçmiş tablosunu yoklarken `fail: ... "__EFMigrationsHistory"` şeklinde tek bir satır loglar; bu beklenen bir durumdur.)

PostgreSQL yok mu? Tamamen bellekte çalıştırın (uygulama kapanınca veri silinir; demo veri her açılışta yeniden yüklenir):

```bash
# bash / zsh
Database__Provider=InMemory dotnet run --project src/OpsFlow.API
```

```powershell
# PowerShell
$env:Database__Provider = "InMemory"; dotnet run --project src/OpsFlow.API
```

```bat
:: cmd.exe
set Database__Provider=InMemory
dotnet run --project src/OpsFlow.API
```

Uygulama <http://localhost:5080> adresinde dinler.

### Testler

```bash
dotnet test                                   # dış servis gerektirmeyen her şey
OPSFLOW_TEST_PG="Host=localhost;Username=postgres;Password=..." dotnet test   # + gerçek PostgreSQL testleri
```

`OPSFLOW_TEST_PG` yoksa 18 PostgreSQL testi atlanır (CI her zaman ayarlar ve atlanan test varsa kırılır). Bağlantı dizesi bir veritabanı adı içermemelidir: her test sınıfı kendi veritabanını oluşturup siler.

## Ücretsiz dağıtım (Render + Neon)

Depoda bir [`render.yaml`](render.yaml) Blueprint'i vardır; veritabanı ücretsiz bir [Neon](https://neon.com) PostgreSQL'idir.

1. **Neon:** bir proje oluşturun (Render'a yakın bir bölge seçin, örn. AWS Frankfurt) ve **doğrudan** bağlantı dizesini kopyalayın ("Connection pooling" işaretini kaldırın). `postgresql://user:password@ep-xxx.neon.tech/neondb?sslmode=require` biçimindedir; olduğu gibi yapıştırın, uygulama URL'yi kendisi çevirir.
2. **Render:** *New -> Blueprint*, bu depoyu seçin, `ConnectionStrings__Default` sorulunca Neon URL'sini yapıştırın ve uygulayın. `Jwt__Key` sizin için üretilir; migration'lar açılışta çalışır. İlk derleme birkaç dakika sürer.
3. Render'ın gösterdiği adresi (`https://opsflow-xxxx.onrender.com`) açın. `Seed__Demo=true` iken (Blueprint varsayılanı) giriş sayfasında demo düğmeleri görünür.

Ücretsiz katmanların pratikteki anlamı:

- **Render'ın ücretsiz web servisi 15 dakika trafik görmezse uyur** ve uyanması yaklaşık bir dakika sürer. Uyurken hiçbir şey çalışmaz; SLA işçisi de. Servis uyanınca bir taramayla geriye kalanı yakalar.
- **Bir "ping" ile uyanık tutmayın.** SLA taraması veritabanını her dakika sorguladığı için veritabanı hiç uykuya geçmez; Neon'un ücretsiz planı ayda yalnızca 100 hesaplama saati içerir (kesintisiz çalışan en küçük hesaplama yaklaşık 180 saat gerektirir) ve veritabanı ayın geri kalanı için askıya alınır.
- **Demo herkese açıktır.** `Seed__Demo=true` demo parolasını giriş sayfasında yayınlar, herkes admin hesabını kullanabilir ve veri sıfırlanmaz. Vitrin dışındaki her şey için `false` yapın; sonra `Platform__ApiKey` (en az 16 karakter) ve `POST /api/platform/tenants` ile kendi kiracınızı oluşturun.
- Render'ın kendi ücretsiz PostgreSQL'i 30 gün sonra sona erer; veritabanının Neon'da olmasının nedeni budur.

Varsayılan dala yapılan her push otomatik dağıtılır (`autoDeployTrigger: commit`).

## Özellikler

1. **Kiracılar**: statik anahtarla korunan, hız sınırlı bir platform ucundan açılır (yapılandırılmadıkça kapalıdır). Her kiracı varsayılan SLA politikaları ve bir admin ile başlar.
2. **Roller**: `Employee` (yalnızca kendi biletleri), `Agent`, `TenantAdmin`.
3. **Biletler**: kiracı başına numaralama (`ACME-0007`), öncelikler, arama/filtre/sayfalama, yorumlar, iç notlar. Talep eden aciliyeti bilet açarken belirtir; sonrasında yalnızca personel değiştirebilir (önceliklendirme).
4. **İş akışı**: `New -> Assigned -> InProgress <-> PendingClient -> Resolved -> Closed`.
5. **SLA**: öncelik başına politika, kiracı admini düzenleyebilir (yeni biletlere uygulanır), canlı geri sayım etiketleri.
6. Bilet başına **denetim zaman çizelgesi**.
7. SignalR ile **canlı güncelleme** (ihlallerde sesli uyarı, varsayılan kapalı).
8. **Admin paneli**: SLA politikaları, kullanıcı ekle/pasifleştir. Pasifleştirme, isteği işleyen düğümde anında etkilidir (önbellekteki erişim kararı silinir, açık soketler kesilir); diğer düğümlerde en fazla 30 saniye.

## Mimari

```mermaid
flowchart LR
    B[Tarayıcı<br/>vanilla JS + SignalR] -->|HTTP + JWT<br/>TLS proxy'de| MW
    subgraph API[OpsFlow.API]
      MW[Auth -> TenantResolution<br/>middleware] --> C[Controllers]
      C --> S
      H[OpsHub<br/>SignalR]
    end
    subgraph INF[OpsFlow.Infrastructure]
      S[TicketService / AuthService / ...] --> DB[(OpsFlowDbContext)]
      DB --- F[[Her ITenantEntity için<br/>global query filter]]
      DB --- I[[Damgalama + Denetim<br/>interceptor'ları]]
      W[SlaMonitoringWorker] --> S2[SlaMonitor<br/>kiracılar arası tarama]
      S2 --> DB
    end
    DB --> PG[(PostgreSQL 16<br/>bileşik kiracı FK'leri)]
    S -->|TicketEvent| H
    S2 -->|TicketEvent| H
    H -->|personel grubu / talep sahibi grubu| B
```

Katmanlar: `OpsFlow.Core` (varlıklar, durum makinesi, SLA kuralları, sözleşmeler; bağımlılık yok), `OpsFlow.Infrastructure` (EF Core, servisler, işçi), `OpsFlow.API` (controller'lar, hub, middleware, statik arayüz). Ayrıntı: [docs/architecture.md](docs/architecture.md).

## Tasarım notları

1. **Neden hem global filtre *hem de* bileşik yabancı anahtar?** Filtre okumaları ve normal yazmaları korur; bileşik FK'ler diğer her şeye karşı korur (bir hata, elle SQL düzeltmesi, filtreyi atlayan gelecekteki bir kod yolu). İki bağımsız katman, ikisi de testli.
2. **Kiracılar arası neden 403 değil 404?** `403`, satırın var olduğunu doğrular. Başka bir kiracının gözünde satır yoktur.
3. **Kiracı neden başlık değil?** Başlık saldırganın kontrolündedir. Kiracı kimliği imzalı token içindeki bir claim'dir; değiştirmek imzayı bozar (payload kurcalanarak test edilmiştir).
4. **Kaçış kapıları nerede serbest?** `IgnoreQueryFilters` yalnızca kiracılar arası SLA taramasında; değiştirilebilir `TenantContext` tipi yalnızca giriş, middleware, sistem işleri ve DI kurulumunda; ham SQL ve toplu `ExecuteUpdate/Delete` hiçbir yerde. Testler bu listeleri (değişken adına değil tip adına bakarak) sabitler ve bir allow-list girdisi eskirse kırılır; yeni bir kaçış kapısı incelemede göze çarpar.
5. **SLA anlık görüntüsü ve pencereleri.** Politikayı düzenlemek mevcut biletleri değiştirmez. Biletin *önceliğini* değiştirmek süreyi mevcut pencerenin başından yeniden hesaplar ve duraklatılan süreyi korur. Çözülmüş/kapalı bileti **yeniden açmak**, yeniden açma anından yeni bir çözüm penceresi başlatır (eski ihlal varsa denetim zaman çizelgesinde görünür kalır); ilk yanıt geçmişi korunur. Müşterinin cevabı duran saati kendiliğinden başlatmaz; yalnızca personelin geçişi başlatır.
6. **Bilet numaraları**, kiracı satırında bir concurrency token ile ve sınırlı, rastgele gecikmeli yeniden denemeyle ayrılır. Basit ve çekişmede doğrudur (PostgreSQL'de 25 paralel oluşturma ile test edildi) ama kiracı başına bilet oluşturmayı sıraya dizer; bir veritabanı sequence'ı daha iyi ölçeklenirdi.
7. **Tek saat.** Her şey `TimeProvider` okur (JWT ömür doğrulaması dahil); testler uyumak yerine zamanı ileri sarar.
8. **İki concurrency token.** İnsanlar `Version`'ı, SLA taraması kendi `SlaRevision`'ını artırır. Tarama `Version`'ı artırsaydı, bir bilet acil olduğu anda açık her düzenleyicinin kopyası eskirdi; hiçbir şeyi artırmasaydı üst üste binen iki tarama aynı olayı iki kez üretebilirdi. (Testler iç içe geçmeyi deterministik olarak zorlar: iki bağlam, biri kaydetmeden önce aynı bileti yükler; gerçek örtüşme ayrıca PostgreSQL'de koşar.)
9. **Katı JSON.** Enum'lar yalnızca isimle taşınır (`"priority": "High"`); sayı `400`'dür. Zorunlu alanlar (`version`, `to`, `isActive` ...) `required` işaretlidir; boş bir `{}` asla sessizce "false" ya da "0" anlamına gelmez.

## İş akışı

```mermaid
stateDiagram-v2
    [*] --> New
    New --> Assigned: atanan zorunlu
    Assigned --> InProgress
    Assigned --> New: atamayı kaldır
    InProgress --> PendingClient: SLA saati durur
    PendingClient --> InProgress: SLA saati devam eder
    InProgress --> Resolved: özet zorunlu
    PendingClient --> Resolved: özet zorunlu
    InProgress --> Assigned
    Resolved --> Closed
    Resolved --> InProgress: talep sahibi çözümü reddeder (yeni SLA penceresi)
    Closed --> InProgress: yalnızca kiracı admini (yeni SLA penceresi)
```

Varsayılan SLA hedefleri (kiracı başına düzenlenebilir):

| Öncelik | İlk yanıt | Çözüm |
| --- | --- | --- |
| Critical | 30 dk | 2 sa |
| High | 1 sa | 8 sa |
| Medium | 4 sa | 24 sa |
| Low | 8 sa | 72 sa |

## Teknoloji yığını

- **Backend:** .NET 10, ASP.NET Core Web API, EF Core 10 + Npgsql, SignalR, BCrypt.Net (enhanced mod), JWT bearer
- **Veritabanı:** PostgreSQL 16 (hızlı testler ve veritabansız mod için in-memory sağlayıcı)
- **Frontend:** semantik HTML, CSS (açık/koyu), vanilla ES2020, SignalR JS istemcisi (projeye dahil, CDN yok)
- **Kalite:** xUnit, `WebApplicationFactory`, uyarılar hata sayılır, NuGet audit, CodeQL, Dependabot

## Güvenlik modeli

| Risk | Karşılık |
| --- | --- |
| Kiracılar arası okuma | Her `ITenantEntity` üzerinde global query filter; kiracı yoksa fail-closed; yabancı kimliklerde `404` |
| Kiracılar arası yazma | Damgalama interceptor'ı yabancı/boş kiracı kimliğini reddeder; `TenantId` değiştirilemez; PostgreSQL'de bileşik FK'ler |
| Sahte kiracı / token | Kiracı yalnızca imzalı JWT'den; başlıklar yok sayılır; yanlış anahtar, yanlış issuer/audience, süresi dolmuş, `alg=none`, eksik veya bozuk kiracı claim'leri `401` döner (her biri test edilmiştir) |
| Kiracı içi yetki | Çalışan yalnızca kendi biletlerini görür, iç notları görmez; rol kuralları servis katmanında (ve admin uçlarında `[Authorize]` ile) uygulanır |
| Parola tahmini | BCrypt (enhanced: uzun parolalar tamamen anlamlı); 5 hata hesabı 15 dk kilitler (paralel tahminde de doğru sayılır); giriş IP başına hız sınırlı; bilinmeyen kiracı, bilinmeyen kullanıcı, pasif kullanıcı, pasif kiracı, kilitli hesap ve yanlış parola aynı yanıtı verir ve her biri bir bcrypt maliyetine mal olur |
| Eskimiş erişim | Kullanıcıyı pasifleştirmek önbellekteki erişim kararını siler ve açık soketlerini tüm gruplardan çıkarır (tek düğüm); diğer düğümler en fazla 30 sn bekler; soket token'ı bitince de kapanır. Pasif kiracı aynı şekilde reddedilir |
| Kayıp güncelleme | İyimser eşzamanlılık `Version` -> `409`; reddedilen eski yazma denetim izinde iz bırakmaz |
| Enjeksiyon / XSS | EF parametreleri; arayüz yalnızca `textContent` ile çizer; sıkı CSP (`script-src 'self'`), `nosniff`, `frame-ancestors 'none'` (hata yanıtlarında da yeniden uygulanır); API verisinde `Cache-Control: no-store` |
| Zayıf veya sızan sırlar | JWT anahtarı kısa/tekrarlıysa, repoda duran geliştirme anahtarı Development dışında kullanılırsa veya platform anahtarı 16 karakterden kısaysa uygulama açılmaz; `appsettings.json`'da sır yok (test edilmiştir); compose `JWT_KEY` ister; demo, istenmedikçe kapalıdır |
| Anonim uçların kötüye kullanımı | Giriş ve platform uçları hız sınırlıdır; platform anahtarı gövde okunmadan önce kontrol edilir; istek gövdesi 1 MB ile sınırlıdır (`413`); bilinmeyen yollar düz `404` döner; güvenilir HTTPS proxy arkasında HSTS |
| Zafiyetli bağımlılık | NuGet audit derlemeyi kırar; CI haftalık yeniden tarar |

## Bilinen sınırlar

Ne olmadığı konusunda dürüst olmak gerekirse:

- **Simüle veri, tek amaçlı.** E-posta, ek dosya, parola sıfırlama, MFA veya SSO yok.
- **İzolasyon uygulama düzeyinde + FK'lerdir.** PostgreSQL Row-Level Security kullanılmıyor; sıradaki sertleştirme adımı o olurdu.
- **SLA duvar saati dakikası kullanır.** Mesai saati, tatil veya saat dilimi yok. Tarama her aralıkta tüm kiracıların açık biletlerini yeniden okur (parti sınırlı, sınıra ulaşınca uyarı verir); demo için yeterli, ölçekte daha akıllı bir sorgu gerekir.
- **Tek düğüm.** SignalR grupları ve erişim önbelleği bellektedir; ölçeklemek için Redis backplane, paylaşımlı iptal ve paylaşımlı hız sınırlayıcı gerekir. Bağlantı kesme ve önbellek silme, isteği işleyen düğümde etkilidir.
- **JWT `sessionStorage`'da durur.** Sıkı CSP XSS riskini azaltır, ancak HttpOnly cookie + refresh token rotasyonu daha güçlü olurdu. Token'lar 30 dakika geçerlidir. WebSocket'te token hub URL'sinin sorgu dizesinde taşınır (tarayıcılar orada başlık koyamaz); bu proxy günlüklerine düşebilir.
- **TLS proxy'nin işidir.** Konteyner düz HTTP konuşur ve yönlendirme yapmaz; `Strict-Transport-Security` başlığını yalnızca güvenilir bir proxy'nin arkasında HTTPS ile gelen isteklere gönderir. `Proxy__TrustForwardedHeaders=true` değerini **yalnızca** güvenilir bir proxy'nin arkasında verin; aksi halde istemciler IP'lerini taklit edip hız sınırlayıcıyı aşabilir. Proxy'nin arkasında bu ayar olmadan ise tüm istemciler tek IP ve tek giriş kovası paylaşır. (Testler her iki durumu, sahte `X-Forwarded-For` dahil, kapsar.)
- **Kilitleme kötüye kullanılabilir.** Bilinen bir e-postaya karşı beş yanlış tahmin o hesabı 15 dakika kilitler; saldırgan bilinen bir hesabı kilitli tutabilir. Hesap başına kilitlemenin olağan ödünleşimidir.
- **Platform anahtarı statik paylaşılan bir sırdır.** Demo başlangıcı için uygun; gerçek bir operatör düzlemi uygun kimlik kullanırdı. Kiracıyı askıya alan bir uç yok (erişim kontrolü `IsActive`'i dikkate alır ama yalnızca veritabanında değiştirilebilir).
- **Denetim kapsamı biletlerdir.** SLA politikası düzenlemeleri ve kullanıcı (de)aktivasyonları kaydedilmez. Sonradan pasifleştirilen bir ajana atanmış biletler, biri yeniden atayana kadar ona atanmış kalır.
- **Arama**, başlık ve numarada büyük/küçük harf duyarsız alt dize eşleşmesidir; ASCII dışı harflerde sonuç veritabanı collation'ına bağlıdır.
- **Bilet oluşturma kiracı başına sıralanır** (bkz. tasarım notu 6).
- **Docker dosyaları sağlanır ve CI'da derlenir**, ancak her platformda denenmemiştir.

## Kurulum ve yapılandırma

Yapılandırma `appsettings*.json` ve ortam değişkenlerinden okunur (iç içe için `__`).

| Değişken | Varsayılan | Amaç |
| --- | --- | --- |
| `ConnectionStrings__Default` | - | PostgreSQL bağlantı dizesi |
| `Jwt__Key` | **zorunlu**, 32+ karakter, tekrarlı değil | Token imzalama anahtarı (repodaki geliştirme anahtarı Development dışında reddedilir) |
| `Jwt__ExpiryMinutes` | `30` | Token ömrü |
| `Database__Provider` | `Postgres` | `Postgres` veya `InMemory` |
| `Database__MigrateOnStartup` | `false` (Development'ta `true`) | Açılışta EF migration'larını uygula |
| `Seed__Demo` | `false` (Development'ta `true`) | İki demo şirketi yükle **ve parolasını yayınla** |
| `Platform__ApiKey` | boş (kapalı) | `POST /api/platform/tenants`'ı açar; en az 16 karakter |
| `Sla__IntervalSeconds` | `60` | SLA tarama aralığı |
| `Sla__BatchSize` | `2000` | Tarama başına incelenen bilet |
| `Sla__WorkerEnabled` | `true` | Arka plan işçisini çalıştır |
| `RateLimit__LoginPerMinute` | `10` | IP başına dakikada giriş denemesi |
| `RateLimit__PlatformPerMinute` | `10` | IP başına dakikada platform ucu denemesi |
| `Security__BcryptWorkFactor` | `12` | BCrypt maliyeti |
| `Proxy__TrustForwardedHeaders` | `false` | **Yalnızca** güvenilir bir ters vekilin arkasında `true` yapın |
| `Proxy__ForwardLimit` | `1` | Uygulamanın önündeki proxy sayısı; istemci adresi `X-Forwarded-For` içinde sağdan o kadar uzaktaki girdidir (Render: `3`) |

### API özeti

JSON gövdeleri camelCase kullanır; enum'lar **isimdir** (`"priority": "High"`). Hatalar, makine tarafından okunabilir bir `code` içeren `application/problem+json`'dır.

| Yöntem | Yol | Kim |
| --- | --- | --- |
| `POST` | `/api/auth/login` | anonim (hız sınırlı) |
| `GET` | `/api/auth/me` | herkes |
| `GET` | `/api/tickets` `?status&priority&assignedToId&breached&mine&search&page&pageSize` | herkes (çalışan: yalnızca kendi) |
| `POST` | `/api/tickets` | herkes |
| `GET` / `PATCH` | `/api/tickets/{id}` | herkes (çalışan: kendi; PATCH'te `version` zorunlu) |
| `POST` | `/api/tickets/{id}/transitions` | iş akışı kurallarına göre (`to` ve `version` zorunlu) |
| `POST` | `/api/tickets/{id}/assign` | personel |
| `GET` / `POST` | `/api/tickets/{id}/comments` | herkes (iç not: personel) |
| `GET` | `/api/tickets/{id}/history` | herkes (çalışan: kendi) |
| `GET` | `/api/dashboard/summary` | personel |
| `GET` / `POST` | `/api/users` | personel listeler / admin oluşturur |
| `PATCH` | `/api/users/{id}/status` | admin (`isActive` zorunlu) |
| `GET` / `PUT` | `/api/sla-policies`, `/api/sla-policies/{priority}` | herkes okur / admin yazar |
| `POST` | `/api/platform/tenants` | `X-Platform-Key` |
| `GET` | `/health`, `/api/meta` | anonim |
| WS | `/hubs/ops` | kimliği doğrulanmış |

## Daha fazla belge

- [Mimari](docs/architecture.md)
- [Tasarım kararları](docs/tasarim-kararlari.md): kararlar ve gerekçeleri
- [Değişiklik günlüğü](CHANGELOG.md) · [Katkı](CONTRIBUTING.md) · [Güvenlik politikası](SECURITY.md)

## Lisans

MIT, bkz. [LICENSE](LICENSE). Yazar: Alonessam.
