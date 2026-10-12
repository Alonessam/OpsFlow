# Tasarım kararları

Bu belge, projedeki önemli tasarım kararlarını, gerekçelerini ve **koddaki/testlerdeki karşılıklarını** özetler. Her madde bir dosyaya ya da teste dayanır.

## 1. Bir şirketin verisinin başka şirkete sızmaması nasıl sağlanıyor?

Üç bağımsız katmanla; her biri testli:

1. **Okuma/yazma:** `OpsFlowDbContext`, her `ITenantEntity` için global query filter ekler (`OnModelCreating` içinde yansımayla; yeni bir varlık unutulamaz). Kiracı çözümlenemezse `Guid.Empty` ile karşılaştırır → boş küme (fail-closed).
2. **Yazma koruması:** `EntityStampingInterceptor` yeni kaydı otomatik damgalar, yabancı/boş kiracıyı reddeder, `TenantId`'yi değiştirilemez yapar.
3. **Veritabanı:** bileşik `(TenantId, Id)` yabancı anahtarlar. Uygulama katmanı atlansa bile PostgreSQL `23503` ile reddeder (`PostgresTests.The_database_itself_refuses_*`; test hangi kısıtın tetiklendiğine de bakar).

Ek olarak kaynak kodu tarayan testler (`ArchitectureGuardTests`) kaçış kapılarını sabitler. Sonuç: bir geliştiricinin `WHERE` yazmayı unutması bu mimaride bir güvenlik açığı değil, test hatasıdır.

## 2. Kiracı neden header'dan alınmıyor?

Header'ı herkes yazabilir. Kiracı kimliği imzalı JWT'nin içindedir; değiştirilirse imza bozulur. Testler: sahte `X-Tenant-Id` hem okumada hem yazmada yok sayılır; payload'ı kurcalanmış, yanlış anahtarla imzalı, `alg=none`, süresi dolmuş veya eksik claim'li token `401` alır.

## 3. Başka şirketin bilet ID'si bilinirse ne olur?

`404`. `403`, "bu bilet var ama yasak" demek olurdu; başka kiracının gözünde o satır yoktur.

## 4. Filtre bilerek atlanacaksa? (SLA işçisi tüm kiracıları tarar)

`IgnoreQueryFilters` yalnızca `SlaMonitor.cs` içinde serbest. `ArchitectureGuardTests` bunu, değiştirilebilir `TenantContext` tipinin görünürlüğünü ve `UseSystem` kullanımını dosya listeleriyle sabitler; ham SQL ve toplu işlemler hiçbir yerde yasaktır. Listeler eskirse (dosya silinirse/adı değişirse) test kırılır; hiç dosya taramazsa da kırılır.

## 5. Eşzamanlı düzenleme ve bilet numaraları

- Biletin `Version` alanı insan düzenlemeleri için concurrency token'dır: eski sürümle gelen istek `409`; sekiz paralel yazardan tam bir tanesi kazanır (PostgreSQL'de test edildi).
- SLA işçisi `Version`'ı **artırmaz**, kendi `SlaRevision` token'ını artırır. Aksi halde bir bilet acil olduğu anda ajanın elindeki sürüm geçersiz kalırdı; hiçbir şey artırmasaydı üst üste binen iki tarama aynı olayı iki kez üretebilirdi.
- Bilet numarası (`ACME-0007`) kiracı satırındaki sayaçla, concurrency token + sınırlı ve rastgele gecikmeli retry ile ayrılır. Gerçek PostgreSQL'de 25 paralel oluşturma boşluksuz ve tekildir.

Not: ilk uygulamada yalnızca concurrency exception'ı yakalıyordum; PostgreSQL'de önce benzersiz indeks ihlali (`23505`) geliyor ve 500 dönüyordu. In-memory testler bunu göstermedi, gerçek veritabanı testi gösterdi. In-memory sağlayıcı benzersiz indeksleri ve transaction'ları uygulamaz; bu yüzden ilgili testler iki sağlayıcıda da (ya da yalnızca PostgreSQL'de) koşar.

## 6. Durum makinesi

`if-else` yığını yerine geçiş matrisi + koruma kuralları; saf fonksiyon (I/O yok, saat yok). Testler beklenen davranışı üretim matrisinden **türetmez**, 11 kenarı ve rol kurallarını açıkça yazar; her (from, to, rol, talep-sahibi) birleşimi kontrol edilir. Arayüzdeki butonlar `AllowedFor(...)` ile aynı kaynaktan gelir; arayüz sunucunun reddedeceği bir hareketi önermez.

Eksik girdi (atanan/özet yok) `400`, izin verilmeyen hareket `409`, yetki yoksa `403` döner.

## 7. SLA nasıl hesaplanıyor?

- `PendingClient` saati durdurur (`PauseSla`); çıkışta süreler duraklama kadar ötelenir (`ResumeSla`). Duraklama, **her süre karşılaştırmasından önce** kredilenir; bu yüzden `PendingClient`'tan doğrudan çözülen bilet yanlış ihlal almaz (bu bir bug'dı, bağımsız inceleme yakaladı, test eklendi).
- Süreler bilet açılırken politikadan **kopyalanır**; politikayı sonradan değiştirmek geçmişi yeniden yazmaz. Öncelik değişirse süreler mevcut pencerenin başından yeniden hesaplanır, duraklatılan süre korunur.
- **Yeniden açma yeni bir çözüm penceresi başlatır.** Aksi halde zamanında çözülmüş bir bilet, ertesi gün "çözüm reddedildi" diye açılınca süresi çoktan dolmuş eski bir son tarihe göre anında ihlal sayılırdı. Eski ihlal (varsa) denetim zaman çizelgesinde kalır.
- Müşterinin cevabı duran saati kendiliğinden başlatmaz; yalnızca personelin geçişi başlatır (bilinçli sadelik).
- Bilet açan, aciliyeti kendisi belirtir; sonradan yalnızca personel değiştirir (önceliklendirme). Bu bilinçli bir karardır ve testle sabitlenmiştir.

## 8. Arka plan işçisi aynı uyarıyı tekrar tekrar göndermez mi?

Göndermez. Her koşul için biletin üzerinde tek seferlik işaret vardır (`SlaWarningAt`, `ResponseBreachedAt`, `SlaBreachedAt`). Önce veritabanına kaydedilir, **sonra** olay yayınlanır. Yayın hatası kaydedilmiş durumu geri almaz ve diğer olayları engellemez (loglanır). Süre ötelenince (öncelik değişimi, duraklamadan dönüş) uyarı işareti temizlenir; yeni pencerede yeniden uyarabilir.

## 9. Denetim izi nasıl garanti ediliyor?

`SaveChangesInterceptor` ile: değişiklikle **aynı transaction** içinde, `[Audited]` işaretli her alan için eski/yeni değer. Servis "log yazmayı unutamaz", çünkü log yazmak servisin işi değildir. SLA işçisinin değişiklikleri `ChangedBy = null` (sistem) olarak kaydedilir. Reddedilen (409) bir yazma iz bırakmaz. Aynı kayıtta yazılan satırlar birer mikrosaniye arayla damgalanır; sıralama GUID şansına kalmaz.

Kapsam bilinçli olarak biletlerle sınırlıdır; SLA politikası ve kullanıcı değişiklikleri kaydedilmez (bkz. README, Bilinen sınırlar).

## 10. SignalR'da bir kiracı diğerinin olaylarını dinleyebilir mi?

Hayır. İstemci grup seçemez: hub'da çağrılabilir metot yoktur; üyelik bağlantı anında doğrulanmış token'dan türetilir. Personel yalnızca kendi kiracısının personel grubuna girer (olayı bir kez duyar); çalışan yalnızca kendi grubuna. Testler: başka kiracının personeli ve aynı kiracıdaki başka çalışan hiçbir olay almaz; talep sahibi iç notları ve SLA alarmlarını almaz; pasifleştirilen kullanıcının açık bağlantısı anında kesilir; token süresi dolunca soket kapanır.

## 11. Bilinen zayıf yanlar

README'deki "Bilinen sınırlar" bölümü güncel listedir. Özetle: Row-Level Security yok; tek düğüm; JWT `sessionStorage`'da; SLA duvar saati; bilet numarası kiracı başına sıralı; kilitleme kötüye kullanılabilir; denetim kapsamı biletlerle sınırlı.

## 12. Test stratejisi hakkında

- Mutasyon denemesi: testleri yazarken üretim koduna bilerek hata sokup (kiracı filtresini kaldırma, yazma korumasını kapatma, denetimi kapatma, çalışan görünürlüğü, iç not filtresi, SLA sırası, yeniden açma, katı enum, kilitleme token'ı, tarama token'ı, bağlantı kesme) en az bir testin kırıldığı doğrulandı. Tarama token'ı mutasyonunu önce yalnızca PostgreSQL testi yakaladı (in-memory sağlayıcı paralel taramaları sırayla çalıştırdığı için yarış oluşmuyordu). Bunun üzerine iki bağlamın aynı bileti kaydetmeden önce yüklediği deterministik testler yazıldı; mutasyon artık in-memory'de de yakalanıyor.
- Gerçek zamanlı testler, olumsuz iddialardan önce dinleyicileri "ısındırır"; sabit bekleme yalnızca "yanlış yönlendirilmiş mesaj gelmedi" kontrolünde kullanılır.
