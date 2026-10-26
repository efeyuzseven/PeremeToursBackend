# Pereme Tours: Koçi entegrasyonu

Koçi, Dentur Avrasya sitesinde olduğu gibi Pereme Tours sitesinde (https://pereme.com.tr)
de ziyaretçilerin turlar hakkındaki sorularını cevaplayacak. Bu doküman ajanın Pereme
verilerini okuyacağı uç noktaları ve kullanım kurallarını anlatır. Makinenin okuyacağı
sözleşme [chatbot-api.openapi.yaml](./chatbot-api.openapi.yaml) dosyasındadır.

## Erişim

- Taban adres: `https://api-peremetours-test.d1-tech.com/api/v1`
  (adındaki "test" tarihsel bir addır; burası canlı servistir)
- Kimlik doğrulama yok: sitenin kendisinin kullandığı herkese açık okuma uç noktalarıdır.
- Hiçbir uç nokta rezervasyon, ödeme veya bilet işlemi yapmaz.
- Yanıtlar JSON, alan adları camelCase. Hatalar `application/problem+json`
  (`title`, `status`, `detail`) biçimindedir.

| Uç nokta | Ne için |
| --- | --- |
| `GET /tours` | Yayındaki turlar ve kategorileri |
| `GET /tours/{externalTourId}/ports` | Turun kalkış iskeleleri |
| `GET /tours/{externalTourId}/availability?departurePortId={id}` | Satıştaki tarih/saatler ve kişi başı fiyatlar |
| `POST /tours/quote` | Seçilen sefer ve bilet adetleri için güncel toplam fiyat |
| `GET /tour-pages/{key}` | Tur ve kategori sayfalarının metinleri (program, dahil olanlar, notlar) |
| `GET /faqs` | Sıkça sorulan sorular (TR/EN) |

## Turlar (23 Ekim 2026 itibarıyla)

Liste her zaman `GET /tours` ile canlı alınmalıdır; aşağısı yalnızca fikir vermek içindir.

| externalTourId | Tur | Kategori | Kalkış iskeleleri | Bilet tipleri |
| --- | --- | --- | --- | --- |
| 2 | Türk Gecesi | turkish-night | Kabataş | Alkolsüz, Alkollü |
| 3 | Sunset - Gün Batımı Turu | sunset | Kabataş, Beşiktaş | İkramsız, İkramlı |
| 42 | Daytime | daytime | Kabataş, Beşiktaş | İkramsız, İkramlı |
| 9 | Üsküdar Boğaz Turu | bosphorus | Üsküdar | Yetişkin |
| 10 | Kabataş Boğaz Turu | bosphorus | Kabataş | Yetişkin |
| 11 | Beşiktaş Boğaz Turu | bosphorus | Beşiktaş | Yetişkin |

## Akışlar

### "Yarın Türk Gecesi var mı, kaç para?"

```text
GET  /tours                                   -> categoryKey=turkish-night olan turu bul (externalTourId=2)
GET  /tours/2/ports                           -> iskeleler (Kabataş, externalPortId=3)
GET  /tours/2/availability?departurePortId=3  -> departures[] içinde date=yarın olanlar, prices[]
```

Örnek availability yanıtı (kısaltılmış):

```json
{
  "externalTourId": 2,
  "tourName": "Türk Gecesi",
  "prices": [
    { "externalPriceId": 145, "passengerType": "Alkolsüz", "passengerTypeEn": "Without Alcohol", "amount": 1150, "currency": "TRY" },
    { "externalPriceId": 146, "passengerType": "Alkollü", "passengerTypeEn": "With Alcohol", "amount": 1750, "currency": "TRY" }
  ],
  "departures": [
    { "externalId": 8387, "externalPortId": 3, "portName": "Kabataş", "date": "2026-10-24", "time": "20:30:00" }
  ],
  "bookingNote": "Otel transfer (servis) hizmetimiz bulunmamaktadır, ulaşım misafire aittir. Lütfen saat 20:30'da kalkış noktasında hazır bulununuz",
  "bookingNoteEn": "Hotel transfer (shuttle) service is not provided; ..."
}
```

Fiyatlar kişi başı ve KDV dahildir. Bir turun birden fazla iskelesi varsa her iskele için
ayrı availability çağrısı yapılır.

### "2 alkolsüz, 1 alkollü toplam ne tutar?"

Kişi başı fiyatlarla kendin çarpma yapmak yerine güncel fiyatı doğrula:

```http
POST /tours/quote
Content-Type: application/json

{
  "externalTourId": 2,
  "externalDeparturePortId": 3,
  "externalDepartureId": 8387,
  "tourDate": "2026-10-24",
  "tickets": [
    { "externalPriceId": 145, "quantity": 2 },
    { "externalPriceId": 146, "quantity": 1 }
  ]
}
```

Yanıttaki `amount` (örnekte 4050 TRY) toplam tutardır. `externalDeparturePortId`
availability sorgusundaki `departurePortId`, `externalDepartureId` ise
`departures[].externalId` değeridir; `tourDate` aynı seferin `date` alanıyla aynı olmalıdır.
Sefer artık satışta değilse 400 döner ve `detail` müşteriye söylenebilir
("Seçilen tarih veya sefer artık kullanılamıyor. Lütfen yeniden seçin.").

### "Türk Gecesi'nde neler var, yemek dahil mi?"

```text
GET /tour-pages/tour-2          -> tek turun sayfası (tour-{externalTourId})
GET /tour-pages/turkish-night   -> kategori sayfası (bosphorus, turkish-night, sunset, daytime)
GET /faqs                       -> genel sorular (kaç dakika önce gelmeliyim vb.)
```

Kullanılacak alanlar: `document.tr.intro`, `document.tr.details` (serbest metin; `##` ile
başlayan satırlar alt başlıktır), `document.tr.bookingNotes` ve `document.sections[]`
içindeki `tr.title`, `tr.body`, `tr.items`, `tr.excludedItems`. İngilizce soru için `en`
alanları kullanılır; `en.details` boşsa Türkçe metin çevrilerek anlatılabilir. Bazı
turların sayfasında henüz özel metin yoktur; o zaman kategori sayfası ve SSS kullanılır.

## Kurallar

- **Tarih ve saat İstanbul saatidir.** "Bugün", "yarın" İstanbul'a göre hesaplanır; saati
  geçmiş seferler önerilmez.
- **Uydurma bilgi yok.** Fiyat, saat ve tarih yalnızca availability/quote yanıtlarından
  söylenir. `departures` veya `prices` boşsa tur şu anda satışta değildir.
- **503 "sefer yok" demek değildir.** Tur sağlayıcısına o an ulaşılamamıştır; müşteriye
  biraz sonra tekrar denemesini veya siteye bakmasını söyle.
- **Her iskele ayrı sorgulanır.** Availability yalnızca sorgulanan iskeleden kalkan
  seferleri döndürür (ör. Sunset için Kabataş 16:30, Beşiktaş 16:40). Tura ait olmayan bir
  iskele için 404 döner.
- **`bookingNote` varsa müşteriye mutlaka aktarılır** (ör. servis yok, kalkıştan önce hazır
  olunması gereken saat).
- **Rezervasyon ve ödeme sohbetten yapılmaz.** Müşteriye aşağıdaki rezervasyon bağlantısı
  verilir; yolcu bilgilerini ve ödemeyi sitede kendisi girer. Kart bilgisi, T.C. kimlik veya
  pasaport numarası sohbette istenmez.
- Quote uç noktasında kaynak IP başına dakikada 30 istek sınırı vardır; 429 dönerse
  bir dakika beklenir.

## Site bağlantıları

| Sayfa | Adres |
| --- | --- |
| Ana sayfa | https://pereme.com.tr/ |
| Tek bir tur | https://pereme.com.tr/tur/{externalTourId} (ör. /tur/2) |
| Boğaz Turu | https://pereme.com.tr/turlar/bogaz-turu |
| Türk Gecesi | https://pereme.com.tr/turlar/turk-gecesi |
| Sunset | https://pereme.com.tr/turlar/sunset |
| Daytime | https://pereme.com.tr/turlar/daytime |
| Sıkça Sorulan Sorular | https://pereme.com.tr/sikca-sorulan-sorular |
| İletişim | https://pereme.com.tr/iletisim |
| KVKK Aydınlatma Metni | https://pereme.com.tr/kvkk-aydinlatma-metni |
| Mesafeli Satış Sözleşmesi | https://pereme.com.tr/mesafeli-satis-sozlesmesi |
| Hesabım (biletlerim, iptal) | https://pereme.com.tr/hesabim |

## Rezervasyon bağlantısı

Tur sayfasının adresine parametre eklenirse sayfa, rezervasyon ekranı seçimler yapılmış
olarak açılır:

```text
https://pereme.com.tr/tur/{externalTourId}?iskele={externalPortId}&tarih={YYYY-MM-DD}&saat={HH:mm}&bilet={externalPriceId}:{adet},{externalPriceId}:{adet}
```

Örnek: 24 Ekim, 20:30 Kabataş Türk Gecesi, 2 alkolsüz ve 1 alkollü bilet:

```text
https://pereme.com.tr/tur/2?iskele=3&tarih=2026-10-24&saat=20:30&bilet=145:2,146:1
```

| Parametre | Değer | Kaynak |
| --- | --- | --- |
| `iskele` | Kalkış iskelesi | `/ports` → `externalPortId` |
| `tarih` | Sefer tarihi, `YYYY-MM-DD` | `departures[].date` |
| `saat` | Kalkış saati, `HH:mm` | `departures[].time` (ilk 5 karakter) |
| `sefer` | İsteğe bağlı; verilirse `saat` yerine bu sefer seçilir | `departures[].externalId` |
| `bilet` | `fiyatId:adet` çiftleri, virgülle ayrılır, toplam en fazla 12 kişi | `prices[].externalPriceId` |

Tüm parametreler isteğe bağlıdır. Geçersiz veya artık satışta olmayan bir değer gelirse
ekran ilk uygun seçeneğe döner; müşteri seçimi orada değiştirebilir. Bağlantı ödeme yapmaz,
yer ayırmaz; müşteriyi yolcu, iletişim ve ödeme adımlarına getirir. Bağlantı verilmeden önce
seçilen seferin availability yanıtında bulunduğu kontrol edilmelidir.

## Sonraki adım: sitedeki sohbet penceresi

Ajan hazır olduğunda Pereme sitesine Dentur'dakine benzer bir sohbet penceresi eklenecek.
Ajan anahtarı tarayıcıya girmeyeceği için istekler Pereme backend'i üzerinden ajanın
`/api/agent-run/query/{agentId}/async` uç noktasına iletilecek. Bunun için Pereme ajanının
kimliği (agent ID) ve anahtarı (agent key) gerekir; anahtar güvenli bir kanaldan
paylaşılmalıdır.
