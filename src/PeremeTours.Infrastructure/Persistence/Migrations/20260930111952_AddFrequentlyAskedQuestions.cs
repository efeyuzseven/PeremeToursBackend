using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PeremeTours.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFrequentlyAskedQuestions : Migration
    {
        private static readonly string[] PublishedSortColumns =
            ["IsPublished", "SortOrder"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FrequentlyAskedQuestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    QuestionTr = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    AnswerTr = table.Column<string>(type: "character varying(3000)", maxLength: 3000, nullable: false),
                    QuestionEn = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    AnswerEn = table.Column<string>(type: "character varying(3000)", maxLength: 3000, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsPublished = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FrequentlyAskedQuestions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FrequentlyAskedQuestions_IsPublished_SortOrder",
                table: "FrequentlyAskedQuestions",
                columns: PublishedSortColumns);

            var seededAt = new DateTimeOffset(
                2026,
                9,
                30,
                0,
                0,
                0,
                TimeSpan.Zero
            );
            migrationBuilder.InsertData(
                table: "FrequentlyAskedQuestions",
                columns:
                [
                    "Id",
                    "QuestionTr",
                    "AnswerTr",
                    "QuestionEn",
                    "AnswerEn",
                    "SortOrder",
                    "IsPublished",
                    "CreatedAtUtc",
                    "UpdatedAtUtc",
                ],
                values: new object[,]
                {
                    {
                        1,
                        "Tur için ne kadar erken gelmeliyim?",
                        "Kalkış noktasında tur saatinden en az 20 dakika önce hazır olmanızı öneririz. Böylece bilet kontrolünü rahatça tamamlayabilirsiniz.",
                        "How early should I arrive for the tour?",
                        "We recommend arriving at the departure point at least 20 minutes before the tour so you can complete ticket checks comfortably.",
                        10,
                        true,
                        seededAt,
                        seededAt,
                    },
                    {
                        2,
                        "Biletimi nasıl kullanabilirim?",
                        "Rezervasyonunuz tamamlandığında iletilen dijital bileti telefonunuzdan göstermeniz yeterlidir. Yazdırmanız gerekmez.",
                        "How do I use my ticket?",
                        "Simply show the digital ticket sent after your reservation on your phone. You do not need to print it.",
                        20,
                        true,
                        seededAt,
                        seededAt,
                    },
                    {
                        3,
                        "Rezervasyonumu iptal edebilir miyim?",
                        "Turdan 24 saat öncesine kadar yapılan iptal talepleri ücretsiz değerlendirilir. Turunuza özel koşulları bilet detaylarından kontrol edebilirsiniz.",
                        "Can I cancel my reservation?",
                        "Cancellation requests made up to 24 hours before the tour are handled free of charge. Please check your ticket details for tour-specific terms.",
                        30,
                        true,
                        seededAt,
                        seededAt,
                    },
                    {
                        4,
                        "Hava koşulları turu etkiler mi?",
                        "Turlar uygun hava ve deniz koşullarında gerçekleştirilir. Güvenlik nedeniyle bir değişiklik olması durumunda ekibimiz sizinle iletişime geçer.",
                        "Can weather conditions affect the tour?",
                        "Tours operate in suitable weather and sea conditions. Our team will contact you if a change is required for safety reasons.",
                        40,
                        true,
                        seededAt,
                        seededAt,
                    },
                    {
                        5,
                        "Çocuklar turlara katılabilir mi?",
                        "Evet, çocuklar aileleriyle birlikte turlarımıza katılabilir. Yaşa göre bilet seçenekleri ve tur özelindeki kurallar rezervasyon ekranında gösterilir.",
                        "Can children join the tours?",
                        "Yes, children may join our tours with their families. Age-based ticket options and tour-specific rules are shown during booking.",
                        50,
                        true,
                        seededAt,
                        seededAt,
                    },
                    {
                        6,
                        "Tur paketine neler dahil?",
                        "Dahil olan hizmetler seçtiğiniz tura göre değişir. Yemek, gösteri veya ikram bilgilerini ilgili turun detaylarında görebilirsiniz.",
                        "What is included in the tour package?",
                        "Included services vary by tour. You can find meal, performance and refreshment details on the selected tour page.",
                        60,
                        true,
                        seededAt,
                        seededAt,
                    },
                }
            );
            migrationBuilder.Sql(
                """
                SELECT setval(
                    pg_get_serial_sequence('"FrequentlyAskedQuestions"', 'Id'),
                    (SELECT MAX("Id") FROM "FrequentlyAskedQuestions")
                );
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FrequentlyAskedQuestions");
        }
    }
}
