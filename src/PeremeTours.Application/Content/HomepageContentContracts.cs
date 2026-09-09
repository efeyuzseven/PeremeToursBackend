namespace PeremeTours.Application.Content;

public sealed record HomepageContentDocument(
    HomepageLanguageContent Tr,
    HomepageLanguageContent En,
    IReadOnlyList<string> InstagramUrls
);

public sealed record HomepageLanguageContent(
    HomepageHeroContent Hero,
    HomepageSectionContent Tours,
    HomepageServicesContent Services,
    HomepageWhyContent Why,
    HomepageStoriesContent Stories,
    HomepageFinalContent Final
);

public sealed record HomepageHeroContent(
    string Lead,
    string Accent,
    string Description
);

public sealed record HomepageSectionContent(
    string Eyebrow,
    string Lead,
    string Accent,
    string Description
);

public sealed record HomepageServicesContent(
    string Eyebrow,
    string Lead,
    string Accent,
    string Description,
    IReadOnlyList<HomepageServiceItem> Items
);

public sealed record HomepageServiceItem(
    string CategoryKey,
    string Title,
    string Description,
    string BrowseLabel,
    string BookingLabel
);

public sealed record HomepageWhyContent(
    string Eyebrow,
    string Lead,
    string Accent,
    string Reviews,
    IReadOnlyList<HomepageBenefitItem> Benefits
);

public sealed record HomepageBenefitItem(string Title, string Description);

public sealed record HomepageStoriesContent(
    string Eyebrow,
    string Lead,
    string Accent,
    string Description,
    string Quote,
    string Meta
);

public sealed record HomepageFinalContent(
    string Pre,
    string Title,
    string Action
);

public interface IHomepageContentService
{
    Task<HomepageContentDocument> GetAsync(
        CancellationToken cancellationToken
    );

    Task<HomepageContentDocument> UpdateAsync(
        HomepageContentDocument document,
        CancellationToken cancellationToken
    );
}
