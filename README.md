# PeremeTours Backend

PeremeTours bilet satış uygulamasının ASP.NET Core Web API backend'i.

## Mimari

Sofistike projesindeki güncel yapıyla aynı yönde Clean Architecture kullanılır:

- `PeremeTours.Api`: HTTP controller'ları, JWT doğrulama ve CORS
- `PeremeTours.Application`: kullanım senaryosu sözleşmeleri
- `PeremeTours.Domain`: kullanıcı, rol ve tur bileti modelleri
- `PeremeTours.Infrastructure`: PostgreSQL, EF Core, login ve admin servisleri
- `tests`: unit ve HTTP integration testleri

Teknolojiler: .NET 10, ASP.NET Core Web API, EF Core, Npgsql/PostgreSQL ve JWT Bearer.

## API uçları

- `POST /api/v1/auth/register`
- `POST /api/v1/auth/login`
- `GET /api/v1/auth/me`
- `GET /api/v1/account/profile`
- `PATCH /api/v1/account/profile`
- `POST /api/v1/account/password`
- `DELETE /api/v1/account`
- `GET /api/v1/account/reservations`
- `GET /api/v1/tours`
- `GET /api/v1/tours/{externalTourId}/ports`
- `GET /api/v1/tours/{externalTourId}/availability?departurePortId=3&saleType=2`
- `POST /api/v1/tours/quote`
- `GET /api/v1/tours/{externalTourId}/image`
- `GET /api/v1/site-content/homepage`
- `GET /api/v1/tour-pages/{key}`
- `GET /api/v1/tour-pages/{key}/images/{imageId}`
- `GET /api/v1/admin/tour-pages`
- `GET /api/v1/admin/tour-pages/{key}`
- `PUT /api/v1/admin/tour-pages/{key}`
- `POST /api/v1/admin/tour-pages/{key}/images`
- `GET /api/v1/faqs`
- `GET /api/v1/admin/tour-contents`
- `PUT /api/v1/admin/tour-contents/{externalTourId}`
- `POST /api/v1/admin/tour-contents/{externalTourId}/image`
- `DELETE /api/v1/admin/tour-contents/{externalTourId}/image`
- `GET /api/v1/admin/site-content/homepage`
- `PUT /api/v1/admin/site-content/homepage`
- `GET /api/v1/admin/faqs`
- `POST /api/v1/admin/faqs`
- `PUT /api/v1/admin/faqs/{id}`
- `DELETE /api/v1/admin/faqs/{id}`
- `GET /api/v1/admin/users`
- `PATCH /api/v1/admin/users/{id}`
- `GET /api/v1/admin/tickets`
- `POST /api/v1/admin/tickets`
- `PATCH /api/v1/admin/tickets/{id}`
- `POST /api/v1/payments/tour/initialize`
- `GET /api/v1/payments/availability`
- `GET /api/v1/payments/tour/status` (`X-Payment-Token` başlığı)
- `POST /api/v1/payments/ziraat/callback`
- `GET /api/v1/system/health`
- `GET /api/v1/system/ready` (veritabanı bağlantısını doğrular; erişilemiyorsa `503`)

Panel uçları geçerli JWT ve ilgili alana ait güncel rol ister. Kullanıcı kapatılır veya rolü değiştirilirse eski token anında reddedilir.

## Panel yetkileri

- **Admin:** Tüm panel uçları; kullanıcı/rol yönetimi, içerikler, manuel bilet işlemleri, iptal/iade ve geri bildirim yanıtları.
- **Editor / Advisor:** Yalnızca `admin/site-content`, `admin/tour-contents`, `admin/tour-pages`, `admin/faqs` okuma/düzenleme/görsel yükleme uçları. İki rol aynı yetkilere sahiptir.
- **Service:** `admin/feedback` liste/detay/yanıt; `admin/ticket-errors` liste; `admin/tickets` liste, iptal sonucu okuma ve açık onaylı tam rezervasyon iptal/iade. Manuel bilet oluşturma ve doğrudan bilet güncelleme kapalıdır.
- **User:** Panel erişimi yoktur; kendi hesabı ve rezervasyonları mevcut akışla kullanılabilir.

Rol atama yalnızca Admin tarafından `PATCH admin/users/{id}` ile yapılır. Kendi Admin rolünü Editor/Advisor/Service/User'a düşürme engellenir. Her rol değişikliği kimlik doğrulama damgasını yeniler ve eski JWT/sıfırlama bağlantılarını geçersiz kılar. İptal/iade ve yanıt gönderiminde güncel aktif kullanıcı rolü ayrıca kontrol edilir; işlemi yapanın kullanıcı kimliği denetim kaydında tutulur. Kayıt olma her zaman User üretir. Rol sütunu metin olduğundan bu değişiklik migration veya mevcut kullanıcıların rollerinde toplu güncelleme gerektirmez.

Hesap yönetimi yalnızca JWT'deki kullanıcı kimliğine işlem yapar; istemciden sahiplik veya rol alınmaz. Profil güncellemesi `firstName`, `lastName`, `email` alanlarını alır; e-posta değişecekse `currentPassword` gerekir. Şifre değişikliği `currentPassword` ve `newPassword` (8–128 karakter, büyük/küçük harf ve rakam), hesap silme ise `currentPassword` ve `confirmed: true` ister. Yazma uçları IP başına 15 dakikada 10 isteğe sınırlıdır ve `no-store` döner. Değişiklikler serializable işlemde, token damgası tekrar doğrulanarak ve özgün satır sürümüyle karşılaştırılarak uygulanır. Şifre/e-posta değişikliği tüm eski JWT'leri ve şifre sıfırlama bağlantılarını geçersiz kılar; sonuç `requiresLogin: true` döner. Ad/soyad değişikliği oturumu kapatmaz.

Hesap silmede profil adı/e-postası anonimleştirilir, parola hash'i temizlenir, `IsActive=false`, `IsDeleted=true` ve `DeletedAtUtc` kaydedilir. Kullanıcı satırı yabancı anahtarlar için tutulur; bilet, yolcu, tahsilat, iptal/iade ve operasyon kayıtları silinmez/değişmez. İşlem banka veya EasyTicket çağrısı yapmaz ve bilet iptali/iade başlatmaz. Silinen hesap yönetici listesine gelmez, yeniden etkinleştirilemez ve aynı e-postayla yeni kayıt eski `UserId` veya rezervasyon sahipliğini devralmaz. Dağıtımın varsayılan yönetici hesabı silinemez/e-postası değiştirilemez; son aktif yönetici silinemez. `GET profile` bu korumaları `canEditEmail`, `canDeleteAccount`, `deletionBlockedReason` alanlarıyla bildirir; bu hesaplar ad/soyad ve şifrelerini değiştirebilir. `AddAccountDeletion` migration'ı yalnızca iki kullanıcı sütunu ekler; mevcut kayıtları silmez.

Kullanıcı yönetimi: `PATCH /api/v1/admin/users/{id}` ad (`firstName`, zorunlu olduğunda 1–100 karakter), soyad (`lastName`, en fazla 100 karakter) ve e-posta (`email`, en fazla 320 karakter) alanlarını kısmi olarak günceller; mevcut `role` / `isActive` alanları korunur. Atlanan veya `null` alan değişmez; boş soyad temizlenir. Ad/soyad kırpılır ve e-posta küçük harfe normalleştirilir. Aktif olmayan hesaplar dahil aynı e-posta başka hesapta kullanılıyorsa `409 USER_EMAIL_ALREADY_EXISTS` döner. Liste ve güncelleme yanıtları `no-store` kullanır; parola/kimlik doğrulama damgası DTO'ya eklenmez.

Ad/soyad değişikliği oturumları etkilemez. E-posta, rol veya aktiflik gerçekten değişirse kimlik doğrulama damgası yenilenir, eski JWT'ler ve kullanılmamış şifre sıfırlama bağlantıları geçersizleşir; parola değişmez. Rezervasyon sahipliği `UserId` ile korunur, geçmiş biletlerin iletişim/yolcu/ödeme bilgileri yeniden yazılmaz. Yönetici kendini pasifleştiremez veya kendi rolünü düşüremez. Aktörün güncel yetkisi ve değişiklikler aynı Serializable işlemde doğrulanır; PostgreSQL eşzamanlılık çakışması `409 USER_UPDATE_CONFLICT` döner, otomatik tekrar edilmez.

`DeploymentBootstrap:AdminEmail` ile tanımlanan başlangıç yöneticisinin e-postası panelden değiştirilemez (`USER_EMAIL_PROTECTED`); ad/soyadı düzenlenebilir. Liste bu hesap için `canEditEmail: false` döndürür. Böylece sonraki bootstrap çalışmasının eski adresle ikinci yönetici hesabı oluşturması önlenir. Diğer yönetici kendi e-postasını değiştirdiğinde panel oturumu kapanır ve yeni adres/mevcut parola ile giriş istenir. Ek tablo veya migration gerekmez.

Tur kataloğu EasyTicket servisinden alınır ve yalnızca Boğaz Turu, Türk Gecesi Dinner Cruise, Sunset ve DayTime kategorileri yayınlanır. API anahtarı backend yapılandırmasındaki `EasyTicket:ApiKey` alanından okunur; frontend'e gönderilmez.

Fiyatlar ve bilet tipleri availability uçlarında EasyTicket'tan önbelleksiz alınır; İngilizce bilet tipi adları ve tur bilet notları da döndürülür. `tours/quote`, tur/kalkış/sefer/tarih ve `{ externalPriceId, quantity }` listesini alır. Seferi İstanbul saatine göre, bilet tiplerini ve toplam 1–12 misafir sınırını doğrular; toplamı güncel API fiyatlarıyla sunucuda hesaplar. Bu uç kişisel bilgi almaz, rezervasyon veya bilet oluşturmaz ve kontenjan ayırmaz.

Admin panelindeki tur içerik düzenlemeleri `externalTourId` üzerinden PostgreSQL'de tutulur. Başlık, açıklama, rozet, görünürlük ve gösterim sırası EasyTicket kaydını değiştirmeden sitede ezilebilir. JPG, PNG ve WebP kapak görselleri private S3 kovasında saklanır ve yalnızca public görsel endpoint'i üzerinden sunulur.

Ana sayfa metinleri, hizmet kartları, Neden Pereme faydaları, hikâye alanı, son rezervasyon çağrısı ve en fazla 6 Instagram Reel/gönderi bağlantısı iki dilde yönetilebilir. İçerik PostgreSQL'de JSONB olarak tutulur; ilk kurulumda yerleşik TR/EN metinleri kullanılır.

Tur sayfaları `TourPages` tablosunda JSONB olarak tutulur. Sayfa anahtarları dört kategori (`turkish-night`, `sunset`, `daytime`, `bosphorus`) ve katalogdaki gerçek turlar için `tour-{externalTourId}` biçimindedir; bu servis yeni tur oluşturmaz. TR/EN başlık, giriş, CTA ve SEO metinleri; ortak kapak düzeni/görseli; en fazla 8 sıralanabilir, gizlenebilir metin/öne çıkanlar/galeri bölümü yönetilir. Öne çıkanlar en fazla 12 madde, galeri en fazla 6 görsel içerir. Metinler HTML olarak çalıştırılmaz; düzen, renk ve görsel bağlantıları sunucuda doğrulanır. Gizlenen/katalogdan kaldırılan turların public detayları `404` döner. Kategori varsayılanları içerik kaydı oluşturmadan sunulur.

Sayfa görselleri mevcut private S3 kovasında `tour-images/pages/{key}/...` alanında (yapılandırılmış prefix altında) saklanır, `TourPageImages` üzerinden sayfa kapsamlı ve değişmez URL ile sunulur. Dosya boyutu (8 MB), MIME türü ve imza kontrol edilir. Görsel yüklemek tek başına kapak/galeri taslağını yayınlamaz; içerik kaydedilmelidir. Sayfa başına en fazla 40 yüklenmiş görsel tutulur; taslaktan kaldırılan görseller yayındaki eski bağlantıları bozmamak için fiziksel olarak silinmez. Fiyat, ödeme, biletleme ve sefer verileri bu içerikten bağımsızdır.

Sıkça sorulan sorular PostgreSQL'de iki dilde tutulur. Yönetici soruları ekleyebilir, düzenleyebilir, sıralayabilir, yayından kaldırabilir veya silebilir; herkese açık uç yalnızca yayındaki soruları döndürür.

## Yerel geliştirme

PostgreSQL bağlantısı varsayılan olarak `appsettings.Development.json` içinde `localhost:5432/peremetours` adresini kullanır. Docker bulunan bir makinede:

```powershell
docker compose up -d postgres
dotnet tool restore
dotnet tool run dotnet-ef database update --project src/PeremeTours.Infrastructure
$env:DeploymentBootstrap__AdminPassword = "StrongTestPassword1"
$env:EasyTicket__ApiKey = "EasyTicket-anahtarı"
dotnet run --project src/PeremeTours.Api -- --bootstrap
dotnet run --project src/PeremeTours.Api
```

Kontroller:

```powershell
dotnet restore PeremeTours.slnx
dotnet build PeremeTours.slnx --configuration Release --no-restore
dotnet test PeremeTours.slnx --configuration Release --no-build
dotnet list PeremeTours.slnx package --vulnerable --include-transitive
```

## AWS canlı ortamı

Backend, diğer projelerde olduğu gibi paylaşılan ALB arkasında ECS/Fargate üzerinde çalışır. PostgreSQL veritabanı private subnetlerde RDS olarak oluşturulur; JWT, veritabanı ve başlangıç admin parolaları Secrets Manager'da tutulur.

Canlı frontend adresi `https://pereme.com.tr`'dir; DNS ve HTTPS geçişi 2026-10-12 tarihinde normal DNS ile doğrulanmıştır. `Payments:Ziraat:FrontendOrigin` ve `MailSettings:WebsiteUrl` ana alan adını kullanır. CORS ve banka iframe/callback politikaları yalnızca açıkça tanımlanmış `https://pereme.com.tr`, `https://www.pereme.com.tr` ve mevcut CloudFront origin'ine izin verir; eski açık oturumlar/ödeme dönüşleri korunur. Callback mesajları `*` hedefi kullanmaz ve istemcinin `Origin`/`Referer` başlığından güvenilir hedef türetmez. Banka callback API URL'si, POS bilgileri ve kart işleme davranışı değişmemiştir. Otomatik örnek bilet oluşturma (`DeploymentBootstrap:SeedDemoTickets`) kapalıdır; mevcut kayıtlar silinmez. AWS kaynak adlarındaki `-test` tarihî adlandırmadır; bu ortam canlı kullanıcı, rezervasyon ve ödeme verileri içerir.

### Şifre sıfırlama

`POST /api/v1/auth/forgot-password` (`email`, `language: tr|en`) kayıtlı/kayıtsız/pasif hesaplar için aynı, önbelleğe alınmayan `202` yanıtını verir. Hesap sorgusu ve mevcut SMTP ayarlarıyla TR/EN mail gönderimi HTTP yanıtından bağımsız, 128 istek kapasiteli bellek kuyruğunda çalışır; dolu kuyrukta `503` döner. İşleyici yeniden başlatılırken henüz işlenmeyen talep kaybolursa kullanıcı yeni bağlantı istemelidir. IP başına 15 dakikada 5 talep, hesap başına 1 dakika bekleme ve saatte 3 mail sınırı vardır. Belirsiz SMTP sonucunda otomatik yeniden gönderim yapılmaz; loglarda adres, şifre veya ham sıfırlama kodu yoktur.

`POST /api/v1/auth/reset-password` (`token`, `password`) 256 bit rastgele kodun SHA-256 özeti üzerinden çalışır; yalnızca özet, kullanıcı bağlantısı ve 30 dakikalık süre veritabanında saklanır. Ham kod yalnızca SMTP gönderimi sırasında bellektedir. Bağlantının origin'i sabit `https://pereme.com.tr` olur; istemciden redirect URL alınmaz. Başarılı sıfırlama tek veritabanı transaction'ında şifre hash'ini ve `AuthenticationStamp` değerini değiştirir, kullanıcıya ait tüm eski bağlantıları tüketir; rolü değiştirmez. Yeni stamp kontrolü eski JWT oturumlarını reddeder. Önceden oluşturulmuş hesapların nullable stamp alanı, ilk sıfırlamaya kadar mevcut oturumları korur. Migration yalnızca nullable kullanıcı sütunu ve `PasswordResetTokens` tablosu ekler; mevcut rezervasyonları değiştirmez. Sıfırlama gönderimi IP başına 15 dakikada 15 denemeyle sınırlıdır.

```powershell
aws login
.\scripts\deploy-infrastructure.ps1
```

İlk altyapı kurulumundan sonra EasyTicket anahtarı, değeri komut satırında yazdırılmadan `/peremetours/test/EASYTICKET_API_KEY` secret'ına kaydedilir. ECS task tanımı bu değeri `EasyTicket__ApiKey` olarak backend'e aktarır:

```powershell
.\scripts\set-easyticket-api-key.ps1
```

Altyapı ilk kez kurulduktan sonra `main-prod` branch'ine yapılan her push GitHub Actions üzerinden image üretir, migration/bootstrap işini çalıştırır ve ECS servisini yayınlar.

- Backend URL: `https://api-peremetours-test.d1-tech.com`
- Başlangıç admin e-postası: `admin@peremetours.com`
- Admin parolası: `.\scripts\get-test-admin-password.ps1`

CloudFormation: `deploy/aws/peremetours-test.yml`

### Dinamik veritabanı şifresi

AWS ortamında `Database:SecretArn` ve `Database:SecretRegion` tanımlanır.
Backend, ECS task rolüyle yalnızca kendi RDS secret'ını okuyabilir; AWS access key
veya sabit veritabanı şifresi uygulamaya verilmez. Npgsql her yeni fiziksel
PostgreSQL bağlantısında Secrets Manager'dan `AWSCURRENT` sürümünü alır.
Havuzdaki açık bağlantılar yeniden kullanılır; her SQL sorgusunda AWS çağrısı yapılmaz.
Şifre değişince uygulamanın yeniden deploy edilmesi gerekmez. Ek bir parola
önbelleği veya SQL/ödeme/biletleme işlemlerini yeniden çalıştıran retry yoktur.
Secrets Manager erişilemezse yeni bağlantı güvenli şekilde başarısız olur;
eski veya kaynak koddaki şifreye dönülmez. Secret JSON'u ve şifre loglanmaz.
Secret ARN verilmediğinde yerel geliştirmedeki mevcut bağlantı ayarları çalışır.
`system/health` uygulama canlılığını, `system/ready` gerçek veritabanı erişimini
kontrol eder. Deploy her iki kontrolü de doğrular.

## Ziraat Sanal POS

PeremeTours ödeme kayıtları aynı POS hesabı kullanılsa bile `PRM-` sipariş
öneki ve `Application=PeremeTours` yapılandırılmış log alanıyla Dentur Avrasya
işlemlerinden ayrılır. Banka API kullanıcı adı uygulamada sabit değildir ve
yalnızca `Payments:Ziraat:ApiUser` secret değeri üzerinden okunur.

POS bilgileri belli olduğunda ekrana veya komut geçmişine yazdırılmadan
Secrets Manager'a kaydedilir:

```powershell
.\scripts\set-ziraat-pos-settings.ps1
```

Ödeme özelliği yerel yapılandırmada varsayılan olarak kapalıdır. AWS ortamında
açmak için aşağıdaki parametre kullanılır. Yapılandırılan banka URL'leri CANLI POS
adresleridir; uygulamanın AWS ortamının `test` olarak adlandırılması tahsilatı test
işlemine dönüştürmez. Kart sahibiyle kontrollü bir 3D Secure alımı ayrıca doğrulanmalıdır:

```powershell
.\scripts\deploy-infrastructure.ps1 -ZiraatPaymentEnabled true
```

Kart numarası, güvenlik kodu ve POS parolaları veritabanına veya uygulama
loglarına yazılmaz.

CC5 ödeme onayındaki banka referansı `HostRefNum` alanından okunur;
`HostLogKey` yalnızca eski uyumlu yanıtlar için alternatif alan olarak desteklenir.
Başarılı yanıt için sipariş kodu eşleşmesi, `Approved` + `00`, onay kodu ve gerçek
banka referansı zorunludur. Eksik/çelişkili yanıtlar `ReviewRequired` kalır;
otomatik tahsilat tekrarı yapılmaz. Yanıt doğrulama hataları yalnızca sabit neden
kodlarıyla loglanır; ham banka XML'i veya parser ayrıntıları loglanmaz.

Ödeme başlatma isteği, sefer/tarih, birden fazla bilet tipi, her biletin yolcusu,
iletişim bilgileri, KVKK metninin okunduğu onayı, son gösterilen `expectedAmount`,
rastgele `attemptId` ve geçici kart bilgilerini alır. Tutar sunucuda EasyTicket'tan
tekrar hesaplanır; fiyat değişirse `409` döner ve tahsilat başlatılmaz. Aynı
`attemptId` için yalnızca bir kayıt açılabilir. Kimlik/doğum bilgileri biletleme
için private, şifreli RDS'de tutulur; public durum ucu kişisel bilgi döndürmez.

İmzalı banka callback'i tutar, mağaza, para birimi ve tam 3D doğrulamasını kontrol
eder. Callback atomik olarak sahiplenilir; yinelenen callback yeniden `Auth`
yapmaz. Onay önce `Paid` olarak kaydedilir, ardından EasyTicket
`POST /api/data/web-bilet-satis` ucu bir kez çağrılır. Satış sözleşmesi
[sağlayıcının Swagger'ı](https://easyticketapi.denturonline.com/swagger/v1/swagger.json)
ve Dentur Avrasya entegrasyonuyla karşılaştırılmıştır. Bu API bir kontenjan
bekletme/ön rezervasyon ucu değildir. Ancak tüm yolcuların GUID ve PNR'ı alınınca
rezervasyon `Confirmed`, biletleme `Issued` olur. Başarılı ödeme bilgisi ayrıca kalıcı mail kuyruğuna alınır.

Belirsiz banka yanıtında ödeme `ReviewRequired` olur; tekrar tahsilat yapılmaz.
EasyTicket başarısız/belirsiz yanıtında ödeme `Paid` kalır, rezervasyon `Pending`,
biletleme `ReviewRequired` olur. Bu kayıtlar admin panelinde görünür; banka ve
EasyTicket sipariş/PO koduyla MANUEL kontrol edilmelidir. Belirsiz satışta otomatik
tekrar deneme, otomatik iade veya otomatik iptal yoktur. Banka onayından sonra
sunucu kapanırsa takılı `Processing`/`Pending` kayıtları da aynı şekilde kontrol edilir.

Backend, banka HTML'ini en fazla 5 dakika ve 8 MB boyut sınırlı, RAM'deki tek
kullanımlık token ile sunar; veritabanına/kalıcı depolamaya yazmaz. Bu sürümün
frame deposu tek ECS instance'ı içindir; yatay ölçekleme öncesinde ayrıca ele alınmalıdır.
Frontend banka sayfasını frontend'den FARKLI API origin'inde,
`allow-forms allow-scripts allow-same-origin` sandbox'lı iframe'de açar.
`allow-same-origin`, bankanın kendi origin'ine yönlendikten sonra cookie/XHR
kullanabilmesi içindir; API origin'i frontend DOM'una/sessionStorage'a erişemez.
Bir postMessage başarı kaydı sayılmaz: gerçek iframe kaynağı kontrol edilir ve
sonuç sunucudan okunur. Sekme yenilenirse yalnızca opak işlem token'ı sessionStorage
üzerinden geri alınır; kart/yolcu bilgileri saklanmaz. Başarısızlığı kesinleşmeyen
işlem için yeni ödeme önerilmez. Ödeme kart alanları sadece fiyat onay ekranında açılır.

## Kullanıcı hesabı ve rezervasyon iptali

`GET /api/v1/account/profile` ve `GET /api/v1/account/reservations` yalnızca aktif,
JWT ile giriş yapmış kullanıcının kendi bilgilerini sunar (`Cache-Control: no-store`).
Rezervasyon sahipliği `UserId` ile doğrulanır; aynı iletişim e-postasına sahip misafir
rezervasyonları doğrulanmamış üyeliğe otomatik bağlanmaz. Kimlik/pasaport, kart/CVC ve
şifre hash'i bu cevaplara dahil edilmez. QR yalnızca geçerli, ödenmiş EasyTicket
biletinin gerçek `ExternalTicketGuid` değeriyle gösterilir; iptal istenince gizlenir.

`POST /api/v1/admin/tickets/{id}/cancel` aktif Admin hesabı, ekrandaki `ticketCode`,
`expectedAmount` ve 1–300 karakter `reason` ister. Tam rezervasyon iptali desteklenir;
kısmi yolcu/tutar iadesi yapılmaz. Önce bankadan salt okunur `ORDERSTATUS QUERY` ile
sipariş, işlem ve tam tutar doğrulanır. Banka durumuna göre `Void` veya tam tutarlı
`Credit` uygulanır. Banka onayından sonra `Cancelled` + `Refunded` ve
`TicketCancellations.Completed` kaydedilir; bu durum **banka iptal/iade onayıdır**,
EasyTicket iptal onayı anlamına gelmez. Kart bilgisi yeniden istenmez.

Tekil `TicketCancellations` kaydı, işlemi yapan admini, nedeni ve aşamaları kalıcı
tutar. Çift tıklama/tekrar istek mevcut kaydı döndürür; banka veya EasyTicket işlemi
tekrarlanmaz. Banka isteğinden önce `BankReversalStartedAtUtc` kalıcı yazılır.
Banka timeout'u, process çökmesi veya belirsiz banka sonucu `ReviewRequired`
gösterir; manuel banka kontrolü gerekir, otomatik iade tekrarı yapılmaz.
Onaylanan iade ile EasyTicket iptal kuyruğu aynı veritabanı kaydında atomik tutulur.
ECS arka plan işleyicisi voucher içeriğini doğrulayıp `web-bilet-iptal` JSON-string
sözleşmesiyle iptal gönderir. `ProviderStatus`, `ProviderFailureCode`, deneme ve
kilit zamanları banka sonucundan bağımsızdır. EasyTicket başarısızlığı başarılı
banka iadesini değiştirmez ve kullanıcı/iptal sonuç ekranında teknik uyarı çıkarmaz;
yalnızca admin hata kayıtlarına yazılır. İade edilmiş rezervasyonun QR'ı gösterilmez.
Salt okunur EasyTicket ön sorgusunda 408/429/500/502/503/504 veya bağlantı hatası
en fazla üç kez, kısa beklemeler ve ayrı beş saniye süre sınırıyla denenir. Bilet
iptali POST'u ve banka `Void`/`Credit` hiçbir zaman otomatik tekrarlanmaz.
Eski sağlayıcı-öncelikli akışta `PROVIDER_CANCELLATION_CHECK_FAILED`,
`PROVIDER_CANCELLATION_UNAVAILABLE`, `PROVIDER_CANCELLATION_UNKNOWN` veya
`PROVIDER_CANCELLATION_REJECTED` ile bankaya hiç iade gönderilmeden durmuş kayıt,
admin tekrar onaylarsa atomik olarak **yalnızca banka adımı** için sahiplenilebilir.
Eski unknown/rejected EasyTicket POST'u tekrar gönderilmez; sağlayıcı sonucu iç
kayıtta tutulur. Banka yeniden sorgulanır; aynı anda gelen ikinci istek mevcut
işlemi döndürür. `canRetryCancellation` ve `canRetry` bu durumu belirtir.
Yeni sağlayıcı kuyruğunda sadece salt okunur ön sorgunun erişilememesi yeniden
denenebilir (üç kuyruk denemesi); belirsiz POST veya worker çökmesi tekrar gönderilmez.
Banka isteği başlamış, tamamlanmış veya banka sonucu belirsiz kayıt tekrar açılmaz.
`GET /api/v1/admin/tickets/{id}/cancellation` mevcut kaydı salt okunur gösterir.
Testler taklit sağlayıcılarla çalışır; gerçek rezervasyon iptal ederek test yapılmaz.

## Ödeme e-postası ve hata kayıtları

### İptal / iade bildirimi

Yeni banka onaylı iptal/iadelerde (`TicketCancellations.Completed`, `Cancelled` + `Refunded`) tekil `CancellationEmails` kaydı aynı veritabanı commit'inde oluşturulur. Mevcut Dentur SMTP hesabıyla rezervasyonun **iletişim e-posta adresine**, TR/EN modern HTML ve düz metin bildirim gider; üyelik adresiyle eşleştirme ve operasyon adreslerine CC/BCC yoktur. Dentur Avrasya'daki gibi mail banka onayından sonra tetiklenir, EasyTicket arka plan kontrolünün bitmesini beklemez. `Void` için ödeme iptali, `Credit` için banka onaylı iade açıklanır; paranın karta kesin yansıdığı, belirli gün sayısı veya EasyTicket iptalinin doğrulandığı iddia edilmez. Mailde rezervasyon, tur, tarih, kişi ve tutar özeti bulunur; kart/kimlik/pasaport/PNR/QR/voucher yer almaz.

Üç dakikalık atomik lease ve sahipliğe bağlı sonuç kaydıyla tek gönderim işleyicisi seçilir. 90 saniyelik SMTP zaman aşımı ve en fazla dört güvenli geçici-hata denemesi vardır; belirsiz gönderim veya işleyici çökmesinde otomatik tekrar yoktur. Mail başarısızlığı banka/bilet statüsünü değiştirmez ve hiçbir banka ya da EasyTicket işlemini tekrarlamaz. `CANCELLATION_EMAIL_*` hata kodları `Email` aşamasında panelden takip edilir. Panel ve kullanıcı hesabı, satın alma mailinden ayrı `cancellationEmailStatus` / `cancellationEmailSentAtUtc` alanlarını gösterir.

Migration yalnızca boş gönderim tablosu oluşturur; geçmiş iptaller geriye dönük kuyruğa alınmaz veya toplu bildirilmez. Eski iptale bildirim için ayrıca açık talep gerekir. Mevcut POS/SMTP bilgileri ve SMTP güvenlik modu korunur. Kontrollerde gerçek iptal/iade başlatılmaz.

### Satın alma bildirimi

`Paid` durumu ile tekil `PaymentEmails` kaydı aynı veritabanı işlemiyle kaydedilir.
ECS içindeki arka plan işleyicisi rezervasyonun **iletişim e-posta adresine**
Türkçe/İngilizce, mobil uyumlu HTML ve düz metin gönderir. SMTP kesintisi banka
durumunu değiştirmez veya yeni tahsilat/bilet satışı başlatmaz. Bilet kesimi
doğrulanmadıysa e-postada yalnızca ödeme alındığı ve biletlerin kontrol edildiği belirtilir.
PNR sadece `Issued` durumda gösterilir; kimlik, pasaport, doğum tarihi, kart ve CVC mailde yer almaz.

Yeni başarılı bilet kesimleri ayrıca bağımsız `ReservationNotifications` kuyruğuna alınır:
`reservation@pereme.com.tr` yalnızca `turkish-night` (Türk Gecesi),
`mcanerdinc@denturavrasya.com` yalnızca `bosphorus` (Boğaz Turu),
`omacit@pereme.com.tr` ise `sunset` ve `daytime` kategorilerini alır.
Bilinmeyen veya boş kategori iç bildirim oluşturmaz; tur adına bakarak alıcı tahmin edilmez.
Kategori ve bilet tipi ödeme başlangıcındaki
güncel sunucu fiyat sorgusundan saklanır; düzenlenebilir tur adına göre yönlendirme yapılmaz.
Alıcılar sırasıyla `MailSettings:ReservationRecipient`, `MailSettings:BosphorusRecipient` ve `MailSettings:SunsetDaytimeRecipient`
ile ayarlanır. Her alıcıya ayrı HTML/düz metin maili gönderilir; müşteri mailine CC/BCC eklenmez.
İç bildirimde rezervasyon, tur, kalkış, kişi sayısı, iletişim, bilet tipi, PNR ve tutar özeti bulunur;
yolcu kimliği, pasaport, doğum tarihi ve kart bilgileri bulunmaz. Alıcı başına tekil outbox,
atomik worker sahiplenmesi, sınırlı güvenli tekrar ve belirsiz gönderimde otomatik tekrar etmeme
kuralları geçerlidir. İç mail hataları admin bilet hata kayıtlarında `Email` aşamasında
`RESERVATION_*` kodlarıyla görünür; müşteri mailinin durumunu, ödemeyi veya bilet kesimini değiştirmez.
Eski satın alımlar geriye dönük bildirilmez; iptal talebi başlamış rezervasyonların bekleyen
iç bildirimleri gönderilmeden atlanır. Gönderim öncesinde kategori/alıcı eşleşmesi tekrar doğrulanır;
eski yönlendirmeye göre yanlış alıcıda bekleyen kayıtlar `Skipped / RECIPIENT_CHANGED` olur.
Bu kayıtlar başka adrese taşınmaz, yeni mail oluşturulmaz; geçmiş gönderilmiş/başarısız/incelemedeki kayıtlar korunur.
Müşteri satın alma/iptal/şifre sıfırlama mailleri, SMTP güvenlik ayarı ve ortak Dentur gönderici hesabı korunur.

`MailSettings` ayarları kullanılır. AWS, Dentur Avrasya ile aynı gönderici hesabını
ve mevcut `/dentur/avrasya/SMTP_PASSWORD` SSM SecureString kaydını referans alır;
şifre kaynak koda veya task definition içindeki düz metin environment listesine yazılmaz.
`MailSettings:SecurityMode` varsayılan olarak `StartTls` kullanır; `SslOnConnect`
TLS modudur. Bu modlarda sertifika/hostname doğrulaması kapatılamaz ve başarısız
bağlantı şifresiz moda düşmez. Sağlayıcının talebi ve işletme sahibinin açık seçimi
ile `None` kullanılabilir: SMTP şifresi ve mail içeriği ağ üzerinde şifrelenmez.
2026-10-07 tarihinde Dentur sağlayıcısının talebiyle test ortamında `None` seçildi.
Bu ayar sadece SMTP'yi etkiler, banka/API HTTPS bağlantılarını değiştirmez.
Mail ayarları ve kullanıcı doğrulaması kontrol edildikten sonra gönderimi etkinleştirin:

```powershell
dotnet run --project src/PeremeTours.Api -- --check-mail
dotnet run --project src/PeremeTours.Api -- --check-mail-auth
.\scripts\deploy-infrastructure.ps1 -MailSendingEnabled true -MailSecurityMode None
```

`--check-mail` sadece seçili moddaki bağlantıyı kontrol eder; kullanıcı doğrulaması
veya mail gönderimi yapmaz. `--check-mail-auth` ayrıca kullanıcı doğrulaması yapar,
ancak mail göndermez. Başarı kodu TLS/şifresiz bağlantıyı açıkça ayırt eder.
AWS ortamında aynı komut mevcut ECS image'ı ile tek seferlik
task olarak çalıştırılabilir. Gönderim kapalıyken yeni başarılı ödemeler kuyrukta korunur.
Altyapı script'i açıkça parametre verilmedikçe mevcut POS/mail aktivasyon durumunu korur.

Bağlantı kesintileri için en fazla dört mail denemesi yapılır. SMTP reddi veya
sertifika/kullanıcı hatası güvenli, sabit açıklamayla kaydedilir. Gönderim sırasında
sonuç belirsizse ya da worker gönderimi bitirmeden kapanırsa `ReviewRequired` olur;
SMTP kaydı kontrol edilmeden otomatik yeniden gönderilmez. SMTP'nin kabulü inbox'a
ulaşmayı garanti etmez; spam/bounce takibi gönderici mail sunucusunun sorumluluğundadır.

Panel ekranı: `/admin/ticket-errors`; Admin ve Service rolleri erişebilir.

### İletişim geri bildirimleri ve admin yanıtları

`GET /api/v1/admin/feedback?search=...&filter=Unanswered&page=1&pageSize=20` ve `GET /api/v1/admin/feedback/{id}` yalnızca güncel, aktif Admin veya Service yetkisiyle erişilebilir; yanıtlar `no-store` olarak sunulur. Yanıt yazılmış filtre değeri `Answered` (mail başarı durumu değil, yanıt oluşturulmuş olması). Detay, orijinal iletişim talebini, iç bildirim durumlarını ve değiştirilemeyen yanıt geçmişini döndürür.

`POST /api/v1/admin/feedback/{id}/replies`: `{ replyId: UUID, expectedRevision: 0, body: "Yanıt" }`, en fazla 6000 karakter; IP başına dakikada 10 işlem sınırı. Alıcı orijinal talebin e-posta adresinden sunucuda alınır, istemciden alıcı/From/CC kabul edilmez. Aktörün güncel Admin yetkisi yeniden doğrulanır. Talebin `ReplyRevision` alanı atomik CAS ile artırılır ve `ContactReplies` yanıt/gönderim kaydı aynı transaction'da eklenir; iki adminin farklı eski taslakları 409 ile ayrılır. Aynı `replyId` ve içerik HTTP yeniden denemesinde yeni mail oluşturmaz. Son yanıt değişmemişse (bilinen `Failed` hariç) yeni anahtar da aynı kaydı döndürür. Önceki `Queued`, `Processing` veya `ReviewRequired` yanıt varken farklı yeni yanıt başlatılmaz. Bilinen başarısız yanıt ancak adminin yeni, açık gönderim işlemiyle tekrar denenebilir.

`ContactReplyEmailWorker` mevcut SMTP bağlantı güvenliği/kimlik ayarlarını kullanır. Mail sadece müşteriye gider; ekip bildirimi alıcılarına kopyalanmaz. TR/EN şablon müşterinin orijinal mesaj diline göre seçilir; kullanıcı ve admin metinleri HTML encode edilir, düz metin alternatifi vardır. `Sent` SMTP kabulünü belirtir, inbox teslim garantisi değildir. Bilinen geçici hatalar en fazla 4 deneme, belirsiz gönderim/süresi dolan lease `ReviewRequired` ve otomatik tekrar yok. Gönderim kullanıcı/admin kimliğini veya içerikleri loglamaz. Migration yalnızca reply alanını, yeni yanıt tablosunu ve indekslerini ekler; eski talepleri silmez, geçmiş mesajlara otomatik yanıt/mail üretmez. Rezervasyon, ödeme ve iptal mail yönlendirmelerinden bağımsızdır.

Ana sayfa CMS: `/api/v1/admin/site-content/homepage` (Admin, Editor, Advisor). Yeni `texts`, `images`, `imagePositions`, `hiddenSections` alanları eskiden kaydedilmiş JSON içerikleriyle uyumludur. Eski istemcinin bu alanları göndermemesi mevcut değerleri silmez. Ana sayfa görsel yükleme `POST /api/v1/admin/site-content/homepage/images`: JPG/PNG/WebP, içerik imzası doğrulanır, en fazla 8 MB. S3 nesneleri rastgele ve değişmez anahtarlarla aynı özel görsel kovasında saklanır; sadece kaydedilmiş JSON içeriği yayınlanır. Eski görseller yüklemede silinmez. Genel içerik `no-store`, görseller `immutable` olarak sunulur.

İletişim talepleri: anonim `POST /api/v1/contact-requests`, 16 KB gövde sınırı, alan doğrulama, honeypot ve IP başına 15 dakikada 5 istek sınırı. `requestId` istemcinin rastgele UUID anahtarıdır; aynı anahtar ve aynı normalize içerik yeniden işlenmez, farklı içerik 409 olur. Talep ve iki e-posta işi tek işlemde PostgreSQL'e kaydedilir. Yanıt 202, **talep alındı** anlamındadır, gelen kutusu teslim garantisi değildir. Alıcılar `MailSettings:ContactRecipients`: `eyilmaz@denturavrasya.com`, `uakseloglu@denturavrasya.com`. Bunlar tur rezervasyonlarının kategori bazlı alıcılarından bağımsızdır. Gönderici mevcut Dentur SMTP hesabı, Reply-To talebin doğrulanmış e-posta adresidir; HTML alanları encode edilir. Her alıcı ayrı işlenir. Bilinen geçici bağlantı/reddetme hataları en fazla 4 kez denenir; belirsiz gönderim veya süresi dolmuş iş `ReviewRequired` olur, otomatik tekrar gönderilmez. İçerik/SMTP ham hataları loglanmaz. Gönderim durumları `ContactEmails` tablosundadır; talep içeriği `ContactRequests` tablosundadır, herkese açık okuma endpoint'i yoktur. Bu değişiklik eski taleplere geriye dönük mail göndermez.
API: `GET /api/v1/admin/ticket-errors?stage=Payment&page=1&pageSize=20&search=PRM-`.
`stage=Cancellation` ile iptal/iade hataları ayrıca filtrelenir.
Ödeme, biletleme ve mail hataları ayrı filtrelenir; rezervasyon/hata koduyla aranır.
Eski başarısız kayıtlar migration ile `IsHistorical=true` olarak taşınır; orijinal
banka mesajı yoksa uydurulmaz. Eski başarılı ödemelere geriye dönük mail gönderilmez.
Tur biletleri listesinde mailin kuyruk/gönderim/hata durumu da görünür.

### Bankada iptal edilmiş, bileti kesilmemiş test ödemesini kapatma

Operasyon komutu yalnızca **bankada daha önce onaylanmış iptali** site kaydıyla
eşleştirir. Yeni tahsilat, banka iptali/iadesi veya EasyTicket satışı başlatmaz.
Sipariş, banka işlem numarası, kuruş bazında tutar ve banka `V/CNCL` durumu
`ORDERSTATUS=QUERY` ile doğrulanmadan kayıt değiştirilmez. Banka sonucu belirsiz,
bileti kesilmiş veya mail kuyruğu olan işlemler otomatik kapatılamaz.

```powershell
dotnet PeremeTours.Api.dll --reconcile-cancelled-payment PRM-SIPARIS-KODU --expected-amount 350.00 --expected-bank-transaction BANKA-ISLEM-NUMARASI
```

AWS'de mevcut ECS task definition ve aynı özel ağ ile tek seferlik task olarak
çalıştırılır; bu bir HTTP endpoint değildir. Sonuç `Status=Cancelled`,
`PaymentStatus=Refunded`, `TicketingStatus=NotRequired` olur. Geçmiş hata kayıtları
ve müşterinin son durumu okuyabilmesi için ödeme kurtarma anahtarı korunur.
Komut tekrar çalıştırıldığında ikinci bir değişiklik/finansal işlem yapmaz.

Banka XML/3D yanıtları bildirdikleri karakter kodlamasıyla okunur. Canlı CC5
endpoint'inin `ISO-8859-9` ve eski `windows-1254` Türkçe kodlamaları için .NET
`CodePagesEncodingProvider` etkinleştirilir; doğrulanamayan kodlama tahsilat onayı
olarak kabul edilmez. Ham banka yanıtı veya kart bilgileri loglara yazılmaz.
