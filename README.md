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
- `GET /api/v1/tours`
- `GET /api/v1/tours/{externalTourId}/ports`
- `GET /api/v1/tours/{externalTourId}/availability?departurePortId=3&saleType=2`
- `POST /api/v1/tours/quote`
- `GET /api/v1/tours/{externalTourId}/image`
- `GET /api/v1/site-content/homepage`
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

`admin` uçları hem geçerli JWT hem de güncel `Admin` rolü ister. Kullanıcı kapatılır veya rolü değiştirilirse eski token anında reddedilir.

Tur kataloğu EasyTicket servisinden alınır ve yalnızca Boğaz Turu, Türk Gecesi Dinner Cruise, Sunset ve DayTime kategorileri yayınlanır. API anahtarı backend yapılandırmasındaki `EasyTicket:ApiKey` alanından okunur; frontend'e gönderilmez.

Fiyatlar ve bilet tipleri availability uçlarında EasyTicket'tan önbelleksiz alınır; İngilizce bilet tipi adları ve tur bilet notları da döndürülür. `tours/quote`, tur/kalkış/sefer/tarih ve `{ externalPriceId, quantity }` listesini alır. Seferi İstanbul saatine göre, bilet tiplerini ve toplam 1–12 misafir sınırını doğrular; toplamı güncel API fiyatlarıyla sunucuda hesaplar. Bu uç kişisel bilgi almaz, rezervasyon veya bilet oluşturmaz ve kontenjan ayırmaz.

Admin panelindeki tur içerik düzenlemeleri `externalTourId` üzerinden PostgreSQL'de tutulur. Başlık, açıklama, rozet, görünürlük ve gösterim sırası EasyTicket kaydını değiştirmeden sitede ezilebilir. JPG, PNG ve WebP kapak görselleri private S3 kovasında saklanır ve yalnızca public görsel endpoint'i üzerinden sunulur.

Ana sayfa metinleri, hizmet kartları, Neden Pereme faydaları, hikâye alanı, son rezervasyon çağrısı ve en fazla 6 Instagram Reel/gönderi bağlantısı iki dilde yönetilebilir. İçerik PostgreSQL'de JSONB olarak tutulur; ilk kurulumda yerleşik TR/EN metinleri kullanılır.

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

## AWS test ortamı

Backend, diğer projelerde olduğu gibi paylaşılan ALB arkasında ECS/Fargate üzerinde çalışır. PostgreSQL veritabanı private subnetlerde RDS olarak oluşturulur; JWT, veritabanı ve başlangıç admin parolaları Secrets Manager'da tutulur.

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

## Ödeme e-postası ve hata kayıtları

`Paid` durumu ile tekil `PaymentEmails` kaydı aynı veritabanı işlemiyle kaydedilir.
ECS içindeki arka plan işleyicisi rezervasyonun **iletişim e-posta adresine**
Türkçe/İngilizce, mobil uyumlu HTML ve düz metin gönderir. SMTP kesintisi banka
durumunu değiştirmez veya yeni tahsilat/bilet satışı başlatmaz. Bilet kesimi
doğrulanmadıysa e-postada yalnızca ödeme alındığı ve biletlerin kontrol edildiği belirtilir.
PNR sadece `Issued` durumda gösterilir; kimlik, pasaport, doğum tarihi, kart ve CVC mailde yer almaz.

`MailSettings` ayarları kullanılır. AWS, Dentur Avrasya ile aynı gönderici hesabını
ve mevcut `/dentur/avrasya/SMTP_PASSWORD` SSM SecureString kaydını referans alır;
şifre kaynak koda veya task definition içindeki düz metin environment listesine yazılmaz.
587/STARTTLS veya 465/TLS zorunludur; sertifika/hostname doğrulaması kapatılamaz.
Mail ayarları doğruysa ve güvenli bağlantı doğrulandıysa gönderimi etkinleştirin:

```powershell
dotnet run --project src/PeremeTours.Api -- --check-mail
.\scripts\deploy-infrastructure.ps1 -MailSendingEnabled true
```

`--check-mail` sadece TLS bağlantısını kontrol eder; kullanıcı doğrulaması veya
mail gönderimi yapmaz. AWS ortamında aynı komut mevcut ECS image'ı ile tek seferlik
task olarak çalıştırılabilir. Gönderim kapalıyken yeni başarılı ödemeler kuyrukta korunur.
Altyapı script'i açıkça parametre verilmedikçe mevcut POS/mail aktivasyon durumunu korur.

Bağlantı kesintileri için en fazla dört mail denemesi yapılır. SMTP reddi veya
sertifika/kullanıcı hatası güvenli, sabit açıklamayla kaydedilir. Gönderim sırasında
sonuç belirsizse ya da worker gönderimi bitirmeden kapanırsa `ReviewRequired` olur;
SMTP kaydı kontrol edilmeden otomatik yeniden gönderilmez. SMTP'nin kabulü inbox'a
ulaşmayı garanti etmez; spam/bounce takibi gönderici mail sunucusunun sorumluluğundadır.

Admin ekranı: `/admin/ticket-errors`; sadece Admin rolü erişebilir.
API: `GET /api/v1/admin/ticket-errors?stage=Payment&page=1&pageSize=20&search=PRM-`.
Ödeme, biletleme ve mail hataları ayrı filtrelenir; rezervasyon/hata koduyla aranır.
Eski başarısız kayıtlar migration ile `IsHistorical=true` olarak taşınır; orijinal
banka mesajı yoksa uydurulmaz. Eski başarılı ödemelere geriye dönük mail gönderilmez.
Tur biletleri listesinde mailin kuyruk/gönderim/hata durumu da görünür.
