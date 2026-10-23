using Microsoft.EntityFrameworkCore.Migrations;
using Zayra.Api.Data;

#nullable disable

namespace Zayra.Api.Migrations
{
    /// <summary>
    /// Data only, no schema change: the documented backfill <see cref="Application.Common.CountryCodeStandard"/> asks
    /// for. Companies and branches store canonical ISO-2, but the company form and both imports validated a code and
    /// then stored it as typed, so "SAU" (a valid ISO-3) was saved as "SAU". Readers that expect ISO-2 then failed:
    /// Setup crashed on the browser's region names (#251), and Saudi-only checks such as the setup readiness
    /// checklist did not treat the company as Saudi. Writers now store <c>CountryCodeStandard.ToStored</c>.
    /// <para>The mapping is CountryCodeStandard's own ISO-3 → ISO-2 table, plus each ISO-2 to itself (fixes case and
    /// padding), plus the two free-text aliases seen in Gulf sheets, KSA and UAE. Anything else is left untouched for
    /// a person to correct: nothing is guessed. Re-runnable: a row already canonical does not match. No updated_at
    /// bump, so an open setup draft does not read this correction as an edit made elsewhere. Down is a no-op: the
    /// original spelling carried no meaning and is not kept.</para>
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(ZayraDbContext))]
    [Migration("20261023140000_NormalizeCompanyCountryCodes")]
    public partial class NormalizeCompanyCountryCodes : Migration
    {
        /// <summary>Accepted spelling → canonical ISO-2. Kept in step with CountryCodeStandard by a test.</summary>
        public const string MappingValues = """
                    ('SAU','SA'), ('ARE','AE'), ('KWT','KW'), ('QAT','QA'), ('BHR','BH'), ('OMN','OM'), ('EGY','EG'), ('JOR','JO'),
                    ('LBN','LB'), ('IRQ','IQ'), ('YEM','YE'), ('SYR','SY'), ('PSE','PS'), ('MAR','MA'), ('DZA','DZ'), ('TUN','TN'),
                    ('LBY','LY'), ('SDN','SD'), ('TUR','TR'), ('IND','IN'), ('PAK','PK'), ('BGD','BD'), ('LKA','LK'), ('NPL','NP'),
                    ('PHL','PH'), ('IDN','ID'), ('USA','US'), ('GBR','GB'), ('CAN','CA'), ('AUS','AU'), ('DEU','DE'), ('FRA','FR'),
                    ('ITA','IT'), ('ESP','ES'), ('NLD','NL'), ('CHE','CH'), ('CHN','CN'), ('JPN','JP'), ('SGP','SG'), ('MYS','MY'),
                    ('ZAF','ZA'), ('NGA','NG'), ('KEN','KE'), ('ETH','ET'), ('SA','SA'), ('AE','AE'), ('KW','KW'), ('QA','QA'),
                    ('BH','BH'), ('OM','OM'), ('EG','EG'), ('JO','JO'), ('LB','LB'), ('IQ','IQ'), ('YE','YE'), ('SY','SY'),
                    ('PS','PS'), ('MA','MA'), ('DZ','DZ'), ('TN','TN'), ('LY','LY'), ('SD','SD'), ('TR','TR'), ('IN','IN'),
                    ('PK','PK'), ('BD','BD'), ('LK','LK'), ('NP','NP'), ('PH','PH'), ('ID','ID'), ('US','US'), ('GB','GB'),
                    ('CA','CA'), ('AU','AU'), ('DE','DE'), ('FR','FR'), ('IT','IT'), ('ES','ES'), ('NL','NL'), ('CH','CH'),
                    ('CN','CN'), ('JP','JP'), ('SG','SG'), ('MY','MY'), ('ZA','ZA'), ('NG','NG'), ('KE','KE'), ('ET','ET'),
                    ('KSA','SA'), ('UAE','AE')
            """;

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "companies", "branches" })
            {
                migrationBuilder.Sql($"""
                    UPDATE {table} AS t
                    SET country_code = m.iso2
                    FROM (VALUES
                    {MappingValues}
                    ) AS m(code, iso2)
                    WHERE upper(btrim(t.country_code)) = m.code
                      AND t.country_code <> m.iso2;
                    """);
            }
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
