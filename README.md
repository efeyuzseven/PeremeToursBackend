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
- `GET /api/v1/admin/users`
- `PATCH /api/v1/admin/users/{id}`
- `GET /api/v1/admin/tickets`
- `POST /api/v1/admin/tickets`
- `PATCH /api/v1/admin/tickets/{id}`
- `GET /api/v1/system/health`

`admin` uçları hem geçerli JWT hem de güncel `Admin` rolü ister. Kullanıcı kapatılır veya rolü değiştirilirse eski token anında reddedilir.

Tur kataloğu EasyTicket servisinden alınır ve yalnızca Boğaz Turu, Türk Gecesi Dinner Cruise, Sunset ve DayTime kategorileri yayınlanır. API anahtarı backend yapılandırmasındaki `EasyTicket:ApiKey` alanından okunur; frontend'e gönderilmez.

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
