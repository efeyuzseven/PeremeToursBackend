using PeremeTours.Application.Content;
using PeremeTours.Application.Tours;

namespace PeremeTours.Infrastructure.Content;

internal static class HomepageContentDefaults
{
    public static HomepageContentDocument Value { get; } = new(
        new HomepageLanguageContent(
            new HomepageHeroContent(
                "Şehri izleme.",
                "Onunla ak.",
                "Boğaz’ın ritmini, gün batımının rengini ve İstanbul’un hiç acele etmeyen halini keşfet."
            ),
            new HomepageSectionContent(
                "Rotanı seç",
                "Boğaz’da senin",
                "anın",
                "Boğaz Turu, Türk Gecesi, Sunset veya DayTime. İstanbul’a bakmanın en güzel halini seç."
            ),
            new HomepageServicesContent(
                "Hizmetlerimiz",
                "İstanbul’u kendi",
                "ritminde yaşa.",
                "Geceden gün batımına, gündüz rotalarından klasik Boğaz turlarına uzanan deneyimini seç.",
                [
                    new HomepageServiceItem(TourCategoryKeys.TurkishNight, "Türk Gecesi", "Akşam yemeği, canlı gösteriler ve Boğaz’ın gece ışıkları.", "İlgili turları gör", "Rezervasyon yap"),
                    new HomepageServiceItem(TourCategoryKeys.Sunset, "Sunset", "İstanbul siluetini altın saatin renkleriyle denizden izle.", "İlgili turları gör", "Rezervasyon yap"),
                    new HomepageServiceItem(TourCategoryKeys.Daytime, "DayTime", "Gün ışığında iki yaka, yalılar ve şehrin kıyı hikâyeleri.", "İlgili turları gör", "Rezervasyon yap"),
                    new HomepageServiceItem(TourCategoryKeys.Bosphorus, "Boğaz Turu", "İstanbul’un simge yapılarını denizden keşfeden klasik rota.", "İlgili turları gör", "Rezervasyon yap"),
                ]
            ),
            new HomepageWhyContent(
                "İçin rahat olsun",
                "Biletini al.",
                "Gerisini akışa bırak.",
                "840+ mutlu misafir",
                [
                    new HomepageBenefitItem("Güvenli rezervasyon", "Şeffaf fiyatlar, anında onay ve güvenli ödeme altyapısı."),
                    new HomepageBenefitItem("Planın değişebilir", "Turdan 24 saat öncesine kadar ücretsiz iptal kolaylığı."),
                    new HomepageBenefitItem("Yanında bir insan var", "Rezervasyon öncesi ve sonrası gerçek ekip desteği."),
                    new HomepageBenefitItem("Seçilmiş deneyimler", "Her tekne ve rota Pereme ekibi tarafından yerinde denenir."),
                ]
            ),
            new HomepageStoriesContent(
                "Instagram’dan Pereme",
                "Boğaz’daki anlara",
                "yakından bak.",
                "PeremeTours Instagram hesabındaki güncel videoları ve misafir anlarını keşfet.",
                "“Gün batımı çok güzeldi ama asıl fark, ekibin küçük detayları düşünmesiydi. Kendimizi turist gibi değil, İstanbul’un misafiri gibi hissettik.”",
                "Ankara · Gün Batımı Turu"
            ),
            new HomepageFinalContent(
                "Sıradaki güzel anın",
                "kıyıda beklemiyor.",
                "Yerini ayır"
            )
        ),
        new HomepageLanguageContent(
            new HomepageHeroContent(
                "Don’t just watch.",
                "Flow with it.",
                "Meet the rhythm of the Bosphorus, the colour of sunset and the unhurried side of Istanbul."
            ),
            new HomepageSectionContent(
                "Choose your route",
                "Find your moment",
                "on the Bosphorus",
                "Bosphorus Cruise, Turkish Night, Sunset or Daytime. Choose your favourite way to see Istanbul."
            ),
            new HomepageServicesContent(
                "Our services",
                "Experience Istanbul",
                "at your own pace.",
                "Choose your experience from dinner shows and sunsets to daytime routes and classic Bosphorus cruises.",
                [
                    new HomepageServiceItem(TourCategoryKeys.TurkishNight, "Turkish Night", "Dinner, live performances and the night lights of the Bosphorus.", "View related tours", "Book now"),
                    new HomepageServiceItem(TourCategoryKeys.Sunset, "Sunset", "Watch Istanbul’s skyline from the water during golden hour.", "View related tours", "Book now"),
                    new HomepageServiceItem(TourCategoryKeys.Daytime, "DayTime", "Two shores, waterfront mansions and local stories in daylight.", "View related tours", "Book now"),
                    new HomepageServiceItem(TourCategoryKeys.Bosphorus, "Bosphorus Cruise", "A classic route past Istanbul’s landmarks and waterfront history.", "View related tours", "Book now"),
                ]
            ),
            new HomepageWhyContent(
                "You’re in good hands",
                "Book your ticket.",
                "Leave the rest to the flow.",
                "840+ happy guests",
                [
                    new HomepageBenefitItem("Secure booking", "Transparent prices, instant confirmation and secure payments."),
                    new HomepageBenefitItem("Plans can change", "Enjoy free cancellation up to 24 hours before your tour."),
                    new HomepageBenefitItem("Real people, right here", "Genuine support before and after your reservation."),
                    new HomepageBenefitItem("Handpicked experiences", "Every boat and route is tested by the Pereme team."),
                ]
            ),
            new HomepageStoriesContent(
                "Pereme on Instagram",
                "See moments from",
                "the Bosphorus.",
                "Discover recent videos and guest moments from the PeremeTours Instagram account.",
                "“The sunset was beautiful, but the real difference was the team’s attention to every small detail. We felt like guests of Istanbul, not tourists.”",
                "Ankara · Sunset Cruise"
            ),
            new HomepageFinalContent(
                "Your next beautiful moment",
                "isn’t waiting ashore.",
                "Save your place"
            )
        ),
        []
    );
}
