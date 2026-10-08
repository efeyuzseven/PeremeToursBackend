using PeremeTours.Application.Content;
using PeremeTours.Application.Tours;

namespace PeremeTours.Infrastructure.Content;

internal static class TourPageDefaults
{
    public static readonly IReadOnlyList<string> Categories = [TourCategoryKeys.TurkishNight, TourCategoryKeys.Sunset,
        TourCategoryKeys.Daytime, TourCategoryKeys.Bosphorus];

    public static TourPageDocument Create(string category, string? titleTr = null, string? titleEn = null,
        string? descriptionTr = null, string? descriptionEn = null)
    {
        var (tr, en, introTr, introEn) = category switch
        {
            TourCategoryKeys.TurkishNight => ("Türk Gecesi", "Turkish Night Dinner Cruise",
                "Boğaz’ın gece ışıkları, akşam yemeği ve gösterilerle İstanbul’u denizden yaşa.",
                "Experience Istanbul from the water with dinner, performances and the night lights of the Bosphorus."),
            TourCategoryKeys.Sunset => ("Sunset", "Sunset Cruise", "Günün en güzel renklerine Boğaz’dan bak. İstanbul siluetini gün batımında keşfet.",
                "See the day's most beautiful colours from the Bosphorus. Discover Istanbul’s skyline at sunset."),
            TourCategoryKeys.Daytime => ("DayTime", "Daytime Cruise", "Gün ışığında iki yaka, tarihi yalılar ve İstanbul’un kıyı hikâyeleri.",
                "Two shores, historic waterfront mansions and the stories of Istanbul in daylight."),
            _ => ("Boğaz Turu", "Bosphorus Cruise", "İstanbul’un saraylarını, köprülerini ve iki kıtayı buluşturan manzarasını denizden keşfet.",
                "Discover Istanbul’s palaces, bridges and the scenery that brings two continents together from the sea."),
        };
        var localizedTr = new TourPageLanguageContent("İSTANBUL’U DENİZDEN KEŞFET", titleTr ?? tr, descriptionTr ?? introTr,
            "Rezervasyon yap", "İlgili turlar", descriptionTr is { Length: <= 320 } ? descriptionTr : introTr);
        var localizedEn = new TourPageLanguageContent("DISCOVER ISTANBUL FROM THE WATER", titleEn ?? en, descriptionEn ?? introEn,
            "Book now", "Related tours", descriptionEn is { Length: <= 320 } ? descriptionEn : introEn);
        return new TourPageDocument("cover", null, localizedTr, localizedEn,
        [
            new("experience", "text", "white", true, [], new("Deneyimi keşfet", introTr, []), new("Discover the experience", introEn, [])),
            new("highlights", "highlights", "blue", true, [],
                new("Yolculuğunu planla", "Sefer ve bilet seçeneklerini rezervasyon ekranında güncel olarak görebilirsin.",
                    ["Boğaz manzarası", "Güncel sefer seçenekleri", "Bilet tipini kendin seç"]),
                new("Plan your journey", "See current departures and ticket options in the booking screen.",
                    ["Bosphorus views", "Current departure options", "Choose your ticket type"])),
        ]);
    }
}
