using System.Net;
using System.Text;
using DoTogether.Application.DTOs;
using DoTogether.Application.Exceptions;
using DoTogether.Application.Interfaces;
using DoTogether.Application.Services;
using DoTogether.Domain.Entities;
using DoTogether.Domain.Enums;
using DoTogether.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Tests;

public class RecipeImportServiceTests : IDisposable
{
  private readonly SqliteConnection _connection;
  private readonly AppDbContext _db;
  private readonly HouseholdAccessService _householdAccess;

  private readonly Guid _householdId = Guid.NewGuid();
  private readonly Guid _userId = Guid.NewGuid();

  public RecipeImportServiceTests()
  {
    _connection = new SqliteConnection("DataSource=:memory:");
    _connection.Open();

    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite(_connection)
        .Options;

    _db = new AppDbContext(options);
    _db.Database.EnsureCreated();

    _householdAccess = new HouseholdAccessService(_db);

    _db.Users.Add(new User { Id = _userId, Username = "importer", DisplayName = "Importer" });
    _db.Households.Add(new Household { Id = _householdId, Name = "Home", TimeZoneId = "Etc/UTC" });
    _db.HouseholdMembers.Add(new HouseholdMember { HouseholdId = _householdId, UserId = _userId, Role = MemberRole.Admin });
    _db.SaveChanges();
  }

  [Fact]
  public async Task PreviewAsync_ParsesStructuredRecipeMetadata()
  {
    var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new StringContent("""
                <html>
                  <head><title>Imported Chili</title></head>
                  <body>
                    <script type="application/ld+json">
                    {
                      "@context": "https://schema.org",
                      "@type": "Recipe",
                      "name": "Imported Chili",
                      "description": "A structured data recipe.",
                      "recipeYield": "4 servings",
                      "prepTime": "PT15M",
                      "cookTime": "PT30M",
                      "totalTime": "PT45M",
                      "keywords": "Dinner, Chili",
                      "author": { "@type": "Person", "name": "Recipe Author" },
                      "image": ["/images/chili.jpg"],
                      "nutrition": {
                        "@type": "NutritionInformation",
                        "calories": "420 calories",
                        "proteinContent": "18 g",
                        "carbohydrateContent": "32 g",
                        "fatContent": "14 g"
                      },
                      "recipeIngredient": [
                        "1 cup rice",
                        "2 tbsp olive oil",
                        "1 onion, diced"
                      ],
                      "recipeInstructions": [
                        { "@type": "HowToStep", "text": "Heat the oil." },
                        { "@type": "HowToStep", "text": "Add onion and simmer." }
                      ]
                    }
                    </script>
                  </body>
                </html>
                """, System.Text.Encoding.UTF8, "text/html")
    });

    var preview = await service.PreviewAsync(
        _householdId,
        _userId,
        new RecipeImportRequestDto("https://93.184.216.34/recipe/chili"),
        CancellationToken.None);

    Assert.Equal("93.184.216.34", preview.SourceDomain);
    Assert.Equal(ImportConfidenceLevel.High, preview.Confidence);
    Assert.False(preview.RequiresManualReview);
    Assert.Empty(preview.Warnings);
    Assert.Equal(RecipeOriginType.ImportedStructuredData, preview.Recipe.Source?.Type);
    Assert.Equal(4, preview.Recipe.Servings);
    Assert.Equal("Recipe Author", preview.Recipe.Source?.Attribution);
    Assert.Equal(3, preview.Recipe.Ingredients.Count);
    Assert.Equal(2, preview.Recipe.Instructions.Count);
    Assert.Equal(15, preview.Recipe.PrepMinutes);
    Assert.Equal(45, preview.Recipe.TotalMinutes);
    Assert.Equal("https://93.184.216.34/images/chili.jpg", preview.Recipe.ImageUrl);
    Assert.Equal(1m, preview.Recipe.Ingredients[0].Quantity);
    Assert.Equal("cup", preview.Recipe.Ingredients[0].Unit);
    Assert.Equal("rice", preview.Recipe.Ingredients[0].Item);
  }

  [Fact]
  public async Task PreviewAsync_ParsesRussianStructuredRecipeMetadata()
  {
    var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new StringContent("""
                <html>
                  <head><title>Борщ</title></head>
                  <body>
                    <script type="application/ld+json">
                    {
                      "@context": "https://schema.org",
                      "@type": "Recipe",
                      "name": "Борщ",
                      "description": "Классический домашний борщ со свеклой.",
                      "recipeYield": "6 порций",
                      "prepTime": "25 минут",
                      "cookTime": "1 час 10 минут",
                      "totalTime": "1 ч. 35 мин.",
                      "keywords": "Суп, Обед, Русская кухня",
                      "author": { "@type": "Person", "name": "Домашний повар" },
                      "image": ["/images/borscht.jpg"],
                      "nutrition": {
                        "@type": "NutritionInformation",
                        "calories": "180 ккал",
                        "proteinContent": "7 г",
                        "carbohydrateContent": "18 г",
                        "fatContent": "8 г"
                      },
                      "recipeIngredient": [
                        "500 г говядины",
                        "1,5 л воды",
                        "2 шт. свеклы",
                        "1 1/2 ст. л. томатной пасты",
                        "3 зубчика чеснока, измельчить",
                        "1-2 лавровых листа"
                      ],
                      "recipeInstructions": [
                        {
                          "@type": "HowToSection",
                          "name": "Подготовка",
                          "itemListElement": [
                            { "@type": "HowToStep", "text": "Нарежьте овощи соломкой." },
                            { "@type": "HowToStep", "text": "Обжарьте свеклу с томатной пастой." }
                          ]
                        },
                        { "@type": "HowToStep", "text": "Варите борщ до готовности и дайте настояться." }
                      ]
                    }
                    </script>
                  </body>
                </html>
                """, Encoding.GetEncoding("windows-1251"), "text/html")
    });

    var preview = await service.PreviewAsync(
        _householdId,
        _userId,
        new RecipeImportRequestDto("https://93.184.216.34/recept/borsh"),
        CancellationToken.None);

    Assert.Equal(ImportConfidenceLevel.High, preview.Confidence);
    Assert.False(preview.RequiresManualReview);
    Assert.Empty(preview.Warnings);
    Assert.Equal("Борщ", preview.Recipe.Name);
    Assert.Equal("Домашний повар", preview.Recipe.Source?.Attribution);
    Assert.Equal(6, preview.Recipe.Servings);
    Assert.Equal(25, preview.Recipe.PrepMinutes);
    Assert.Equal(70, preview.Recipe.CookMinutes);
    Assert.Equal(95, preview.Recipe.TotalMinutes);
    Assert.Equal(180, preview.Recipe.Nutrition?.Calories);
    Assert.Equal(7m, preview.Recipe.Nutrition?.ProteinGrams);
    Assert.Contains("Русская кухня", preview.Recipe.Tags ?? []);

    Assert.Equal("г", preview.Recipe.Ingredients[0].Unit);
    Assert.Equal("говядины", preview.Recipe.Ingredients[0].Item);
    Assert.Equal(1.5m, preview.Recipe.Ingredients[1].Quantity);
    Assert.Equal("л", preview.Recipe.Ingredients[1].Unit);
    Assert.Equal("воды", preview.Recipe.Ingredients[1].Item);
    Assert.Equal("шт.", preview.Recipe.Ingredients[2].Unit);
    Assert.Equal("свеклы", preview.Recipe.Ingredients[2].Item);
    Assert.Equal(1.5m, preview.Recipe.Ingredients[3].Quantity);
    Assert.Equal("ст. л.", preview.Recipe.Ingredients[3].Unit);
    Assert.Equal("томатной пасты", preview.Recipe.Ingredients[3].Item);
    Assert.Equal("зубчик", preview.Recipe.Ingredients[4].Unit);
    Assert.Equal("чеснока", preview.Recipe.Ingredients[4].Item);
    Assert.Equal("измельчить", preview.Recipe.Ingredients[4].Note);
    Assert.Equal(1.5m, preview.Recipe.Ingredients[5].Quantity);

    Assert.Equal(3, preview.Recipe.Instructions.Count);
    Assert.Equal("Подготовка", preview.Recipe.Instructions[0].Section);
    Assert.Equal("Нарежьте овощи соломкой.", preview.Recipe.Instructions[0].Text);
  }

  [Fact]
  public async Task PreviewAsync_SniffsMetaCharsetForRussianPages()
  {
    var html = """
          <html>
            <head><meta charset="windows-1251"><title>Пирожки</title></head>
            <body>
            <script type="application/ld+json">
            {
              "@context": "https://schema.org",
              "@type": "Recipe",
              "name": "Пирожки",
              "recipeYield": "8 порций",
              "recipeIngredient": ["500 г муки", "250 мл молока"],
              "recipeInstructions": ["Замесите тесто.", "Испеките пирожки."]
            }
            </script>
            </body>
          </html>
          """;
    var content = new ByteArrayContent(Encoding.GetEncoding("windows-1251").GetBytes(html));
    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");

    var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = content
    });

    var preview = await service.PreviewAsync(
      _householdId,
      _userId,
      new RecipeImportRequestDto("https://93.184.216.34/recept/pirozhki"),
      CancellationToken.None);

    Assert.Equal("Пирожки", preview.Recipe.Name);
    Assert.Equal("г", preview.Recipe.Ingredients[0].Unit);
    Assert.Equal("муки", preview.Recipe.Ingredients[0].Item);
  }

  [Fact]
  public async Task PreviewAsync_UsesJinaReaderWhenDirectFetchIsBlocked()
  {
    var service = CreateService(request =>
    {
      if (request.RequestUri?.Host == "r.jina.ai")
      {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
          Content = new StringContent("""
                Title: Сырники
                URL Source: https://93.184.216.34/recept/syrniki
                Markdown Content:
                # Сырники
                ## Ингредиенты
                - 400 г творога
                - 1 яйцо
                - 2 ст. л. сахара
                ## Приготовление
                1. Смешайте творог, яйцо и сахар.
                2. Обжарьте сырники до золотистой корочки.
                """, Encoding.UTF8, "text/plain")
        };
      }

      return new HttpResponseMessage(HttpStatusCode.Forbidden)
      {
        Content = new StringContent("blocked")
      };
    }, enableReaderFallback: true);

    var preview = await service.PreviewAsync(
      _householdId,
      _userId,
      new RecipeImportRequestDto("https://93.184.216.34/recept/syrniki"),
      CancellationToken.None);

    Assert.Equal("Сырники", preview.Recipe.Name);
    Assert.Equal(ImportConfidenceLevel.Low, preview.Confidence);
    Assert.True(preview.RequiresManualReview);
    Assert.Contains(preview.Warnings, warning => warning.Code == "heuristic_recipe_import");
    Assert.Equal("93.184.216.34", preview.SourceDomain);
    Assert.Equal("г", preview.Recipe.Ingredients[0].Unit);
  }

  [Fact]
  public async Task PreviewAsync_SkipsMarkdownNavigationLinksInReaderSections()
  {
    var service = CreateService(request =>
    {
      if (request.RequestUri?.Host == "r.jina.ai")
      {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
          Content = new StringContent("""
                Title: Easy Meatloaf
                URL Source: https://93.184.216.34/recipe/meatloaf
                Markdown Content:
                # Easy Meatloaf
                ## Ingredients
                - [Chicken](https://example.com/chicken)
                - [Beef](https://example.com/beef)
                - Occasions
                - Cuisines
                - Kitchen Tips
                - 1 pound ground beef
                - 1 egg
                ## Directions
                1. Mix ingredients together.
                2. Bake until cooked through.
                """, Encoding.UTF8, "text/plain")
        };
      }

      return new HttpResponseMessage(HttpStatusCode.Forbidden)
      {
        Content = new StringContent("blocked")
      };
    }, enableReaderFallback: true);

    var preview = await service.PreviewAsync(
      _householdId,
      _userId,
      new RecipeImportRequestDto("https://93.184.216.34/recipe/meatloaf"),
      CancellationToken.None);

    Assert.Equal("Easy Meatloaf", preview.Recipe.Name);
    Assert.Equal(2, preview.Recipe.Ingredients.Count);
    Assert.DoesNotContain(preview.Recipe.Ingredients, ingredient => ingredient.RawText.Contains('[', StringComparison.Ordinal));
    Assert.DoesNotContain(preview.Recipe.Ingredients, ingredient => ingredient.RawText is "Occasions" or "Cuisines" or "Kitchen Tips");
    Assert.Equal("ground beef", preview.Recipe.Ingredients[0].Item);
  }

  [Fact]
  public async Task PreviewAsync_UsesReaderTextBeforeNoisyDirectHeuristics()
  {
    var service = CreateService(request =>
    {
      if (request.RequestUri?.Host == "r.jina.ai")
      {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
          Content = new StringContent("""
                Title: Easy Meatloaf
                URL Source: https://93.184.216.34/recipe/meatloaf
                Markdown Content:
                # Easy Meatloaf
                ## Ingredients
                1/2x
                1x
                2x
                Oops! Something went wrong.
                Original recipe (1X) yields 8 servings
                15 mins
                1 hr
                1 (9x5-inch) meatloaf
                · For the loaf**: ground beef, an egg, an onion, milk, bread crumbs, salt, and pepper
                - 1 1/2 pounds ground beef
                - 1 large egg
                - salt and pepper to taste
                ## Directions
                1. Mix ingredients together.
                ![Image 1: Meatloaf](https://example.com/meatloaf.jpg)
                Grant Webster / Food Styling: Lauren McAnelly / Prop Styling: Gabriel Greco
                2. Bake until cooked through.
                ### Cook's Note
                You can use crushed crackers instead of bread crumbs.
                Nutrition Facts (per serving)
                372 Calories
                """, Encoding.UTF8, "text/plain")
        };
      }

      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent("""
              <html>
                <head><title>Easy Meatloaf</title></head>
                <body>
                  <main>
                    <h2>Ingredients</h2>
                    <p>(9,349)</p>
                    <p>7,199 Reviews</p>
                    <p>1,041 Photos</p>
                    <p>This meatloaf recipe doesn't take long to make at all, and it's very good!</p>
                    <h2>Directions</h2>
                    <p>Read the whole article before cooking.</p>
                  </main>
                </body>
              </html>
              """, Encoding.UTF8, "text/html")
      };
    }, enableReaderFallback: true);

    var preview = await service.PreviewAsync(
      _householdId,
      _userId,
      new RecipeImportRequestDto("https://93.184.216.34/recipe/meatloaf"),
      CancellationToken.None);

    Assert.Equal("Easy Meatloaf", preview.Recipe.Name);
    Assert.Equal(3, preview.Recipe.Ingredients.Count);
    Assert.DoesNotContain(preview.Recipe.Ingredients, ingredient => ingredient.RawText.Contains("Reviews", StringComparison.Ordinal));
    Assert.Equal("ground beef", preview.Recipe.Ingredients[0].Item);
    Assert.Equal("large egg", preview.Recipe.Ingredients[1].Item);
    Assert.Equal("salt and pepper to taste", preview.Recipe.Ingredients[2].Item);
    Assert.Equal(2, preview.Recipe.Instructions.Count);
  }

  [Fact]
  public async Task PreviewAsync_UsesLlmFallbackWhenNoRecipeSectionsExist()
  {
    var extractor = new StubRecipeLlmExtractor(new RecipeLlmExtractionResult(
      "Оладьи",
      "Пышные домашние оладьи.",
      ["250 мл кефира", "1 яйцо", "200 г муки"],
      ["Смешайте ингредиенты.", "Жарьте на сковороде."],
      4,
      "4 порции",
      null,
      null,
      25,
      null,
      null,
      ["Завтрак"],
      "LLM Test"));

    var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new StringContent("""
            <html><head><title>Page without recipe schema</title></head>
            <body><main>Long article about breakfast with the actual recipe buried in prose.</main></body></html>
            """, Encoding.UTF8, "text/html")
    }, extractor);

    var preview = await service.PreviewAsync(
      _householdId,
      _userId,
      new RecipeImportRequestDto("https://93.184.216.34/recept/oladi"),
      CancellationToken.None);

    Assert.Equal("Оладьи", preview.Recipe.Name);
    Assert.Equal(ImportConfidenceLevel.Medium, preview.Confidence);
    Assert.True(preview.RequiresManualReview);
    Assert.Contains(preview.Warnings, warning => warning.Code == "llm_recipe_import");
    Assert.Equal("LLM Test", preview.Recipe.Source?.Attribution);
    Assert.Contains("Завтрак", preview.Recipe.Tags ?? []);
  }

  [Fact]
  public async Task PreviewAsync_ReturnsWarningsForPartialParse()
  {
    var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new StringContent("""
                <html>
                  <body>
                    <script type="application/ld+json">
                    {
                      "@context": "https://schema.org",
                      "@type": "Recipe",
                      "name": "Loose Salad",
                      "recipeIngredient": ["salt to taste", "lettuce"],
                      "recipeInstructions": "Toss everything together."
                    }
                    </script>
                  </body>
                </html>
                """, System.Text.Encoding.UTF8, "text/html")
    });

    var preview = await service.PreviewAsync(
        _householdId,
        _userId,
        new RecipeImportRequestDto("https://93.184.216.34/recipe/salad"),
        CancellationToken.None);

    Assert.True(preview.RequiresManualReview);
    Assert.NotEmpty(preview.Warnings);
    Assert.Contains(preview.Warnings, w => w.Code == "ingredient_normalization_partial");
    Assert.Contains(preview.Warnings, w => w.Code == "missing_description");
    Assert.Contains(preview.Warnings, w => w.Code == "missing_servings");
    Assert.Null(preview.Recipe.Servings);
  }

  [Fact]
  public async Task PreviewAsync_RejectsUnsupportedPrivateAddresses()
  {
    var service = CreateService(_ => throw new InvalidOperationException("Should not reach HTTP."));

    var ex = await Assert.ThrowsAsync<ApiProblemException>(() =>
        service.PreviewAsync(
            _householdId,
            _userId,
            new RecipeImportRequestDto("http://127.0.0.1/recipe"),
            CancellationToken.None));

    Assert.Equal(400, ex.StatusCode);
    Assert.Equal("unsupported_domain", ex.ErrorCode);
  }

  [Fact]
  public async Task PreviewAsync_ReportsBlockedFetches()
  {
    var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
    {
      Content = new StringContent("blocked")
    });

    var ex = await Assert.ThrowsAsync<ApiProblemException>(() =>
        service.PreviewAsync(
            _householdId,
            _userId,
            new RecipeImportRequestDto("https://93.184.216.34/recipe/blocked"),
            CancellationToken.None));

    Assert.Equal(502, ex.StatusCode);
    Assert.Equal("recipe_import_fetch_blocked", ex.ErrorCode);
  }

  [Fact]
  public async Task PreviewAsync_ReportsFetchTimeouts()
  {
    var service = CreateService((_, _) => throw new TaskCanceledException("timed out"));

    var ex = await Assert.ThrowsAsync<ApiProblemException>(() =>
      service.PreviewAsync(
        _householdId,
        _userId,
        new RecipeImportRequestDto("https://93.184.216.34/recipe/slow"),
        CancellationToken.None));

    Assert.Equal(502, ex.StatusCode);
    Assert.Equal("recipe_import_fetch_failed", ex.ErrorCode);
  }

  [Fact]
  public async Task PreviewAsync_RejectsRedirectsToUnsupportedPrivateAddresses()
  {
    var requestCount = 0;
    var service = CreateService(request =>
    {
      requestCount++;
      return new HttpResponseMessage(HttpStatusCode.Redirect)
      {
        Headers = { Location = new Uri("http://127.0.0.1/recipe") }
      };
    });

    var ex = await Assert.ThrowsAsync<ApiProblemException>(() =>
      service.PreviewAsync(
        _householdId,
        _userId,
        new RecipeImportRequestDto("https://93.184.216.34/recipe/redirect"),
        CancellationToken.None));

    Assert.Equal(400, ex.StatusCode);
    Assert.Equal("unsupported_domain", ex.ErrorCode);
    Assert.Equal(1, requestCount);
  }

  public void Dispose()
  {
    _db.Dispose();
    _connection.Dispose();
    GC.SuppressFinalize(this);
  }

  private RecipeImportService CreateService(Func<HttpRequestMessage, HttpResponseMessage> responder, bool enableReaderFallback = false)
    => CreateService((request, _) => responder(request), new NullRecipeLlmExtractor(), enableReaderFallback);

  private RecipeImportService CreateService(Func<HttpRequestMessage, HttpResponseMessage> responder, IRecipeLlmExtractor extractor)
    => CreateService((request, _) => responder(request), extractor);

  private RecipeImportService CreateService(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
      => CreateService(responder, new NullRecipeLlmExtractor());

  private RecipeImportService CreateService(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder, IRecipeLlmExtractor extractor, bool enableReaderFallback = false)
  {
    var handler = new StubHttpMessageHandler(responder);
    var client = new HttpClient(handler);
    return new RecipeImportService(client, _householdAccess, extractor,
        new RecipeImportOptions { EnableReaderFallback = enableReaderFallback });
  }

  private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    => Task.FromResult(responder(request, cancellationToken));
  }

  private sealed class StubRecipeLlmExtractor(RecipeLlmExtractionResult result) : IRecipeLlmExtractor
  {
    public Task<RecipeLlmExtractionResult?> ExtractAsync(string pageText, Uri sourceUri, CancellationToken ct)
      => Task.FromResult<RecipeLlmExtractionResult?>(result);
  }
}