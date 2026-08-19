# DoTogether — API Specification

Base URL: `http://localhost:8080/api`

All endpoints except Auth require `Authorization: Bearer <access_token>`.

---

## Auth

Auth routes use the exact casing `/api/Auth/...`. Route matching itself is
case-insensitive, but the refresh token is delivered as an HttpOnly cookie
scoped to `Path=/api/Auth`, and browsers match cookie paths case-sensitively —
clients that call `/api/auth/refresh` will not send the cookie. Always use
`/api/Auth`.

### POST `/api/Auth/register`

Register a user and receive tokens.

**Request:**

```json
{ "username": "alice", "displayName": "Alice", "password": "P@ssword123!" }
```

**Response 201:**

```json
{
  "accessToken": "eyJ...",
  "refreshToken": null,
  "accessTokenExpiresAtUtc": "2025-07-15T12:00:00Z",
  "refreshTokenExpiresAtUtc": "2025-08-14T12:00:00Z",
  "user": {
    "id": "aaa...",
    "username": "alice",
    "displayName": "Alice"
  }
}
```

### POST `/api/Auth/login`

Log in with username and password.

**Request:**

```json
{ "username": "alice", "password": "P@ssword123!" }
```

**Response 200:** Same shape as register.

### POST `/api/Auth/refresh`

Rotate the refresh token. The token is read from the HttpOnly refresh cookie
when present; a JSON body is the fallback.

**Request:**

```json
{ "refreshToken": "base64..." }
```

**Response 200:** Same shape as register.

### POST `/api/Auth/logout`

Requires `Authorization: Bearer <access_token>`. Revokes the current refresh
token (or, if the cookie is missing, every active refresh token for the user)
and clears the refresh cookie.

**Response 204.**

---

## Households

### POST `/api/households`

Create a household (caller becomes Admin).

**Request:**

```json
{ "name": "Our Home", "timeZoneId": "America/New_York" }
```

**Response 201:**

```json
{
  "id": "ccc...",
  "name": "Our Home",
  "timeZoneId": "America/New_York",
  "members": [
    {
      "userId": "aaa...",
      "displayName": "Alice",
      "username": "alice",
      "role": 0,
      "joinedAtUtc": "..."
    }
  ]
}
```

### GET `/api/households`

List households for the current user.

**Response 200:** Array of household objects.

### GET `/api/households/{householdId}`

Get a single household.

New households receive an invite token automatically when they are created.

### PATCH `/api/households/{householdId}`

Update household details (Admin only).

**Request:**

```json
{ "name": "Renamed Home" }
```

**Response 200:** Household object.

### GET `/api/households/{householdId}/invites/current`

Get the current invite token (any member). If an older household does not have an active token yet, one is created automatically.

**Response 200:**

```json
{ "inviteId": "...", "token": "abc123...", "expiresAtUtc": "..." }
```

### POST `/api/households/{householdId}/invites`

Regenerate the invite token (any member). The previous active token is invalidated.

**Request:**

```json
{}
```

**Response 201:**

```json
{ "inviteId": "...", "token": "abc123...", "expiresAtUtc": "..." }
```

### PATCH `/api/households/{householdId}/members/{memberUserId}/role`

Update a household member role (Admin only). The household must always keep at least one admin.

**Request:**

```json
{ "role": 0 }
```

`role` values:

- `0` = Admin
- `1` = Member

**Response 200:** Household object with updated member roles.

### DELETE `/api/households/{householdId}/members/{memberUserId}`

Remove a household member (Admin only). Admins cannot remove themselves — use the leave action instead.

**Response 200:** Household object with updated members.

### POST `/api/households/{householdId}/leave`

Leave the household (any member). If the last member leaves, the household and its invites are soft-deleted; if the departing member was the only admin, the longest-standing remaining member is promoted.

**Response 204.**

### POST `/api/households/join`

Join using an invite token.

**Request:**

```json
{ "inviteToken": "abc123..." }
```

**Response 200:** Household object with updated members.

---

## Chore Templates

### POST `/api/households/{householdId}/chores/templates`

Create a chore template and generate initial occurrences.

**Request:**

```json
{
  "title": "Do the dishes",
  "description": "Wash all dishes",
  "recurrenceRule": { "type": 1, "interval": 1 },
  "assigneeId": "aaa...",
  "startDate": "2025-06-15",
  "endDate": null
}
```

`recurrenceRule.type`: 0=Once, 1=Daily, 2=Weekly, 3=Monthly.
For Weekly, include `daysOfWeek` (ISO 1–7). For Monthly, include `dayOfMonth` (1–31).

**Response 201:**

```json
{
  "id": "ddd...",
  "title": "Do the dishes",
  "description": "Wash all dishes",
  "recurrenceRule": {
    "type": 1,
    "interval": 1,
    "daysOfWeek": [],
    "dayOfMonth": null
  },
  "assigneeId": "aaa...",
  "assigneeName": "Alice",
  "startDate": "2025-06-15",
  "endDate": null,
  "isActive": true,
  "createdAtUtc": "..."
}
```

### GET `/api/households/{householdId}/chores/templates`

List all templates.

### GET `/api/households/{householdId}/chores/templates/{templateId}`

Get a single template.

### PATCH `/api/households/{householdId}/chores/templates/{templateId}`

Partial update.

**Request (all fields optional):**

```json
{ "title": "Updated title", "isActive": false }
```

### DELETE `/api/households/{householdId}/chores/templates/{templateId}`

Soft-delete a template. **Response 204.**

---

## Occurrence Generation

### POST `/api/households/{householdId}/chores/generate`

Trigger rolling generation of occurrences for all active templates (next 90 days).
**Response 204.**

---

## Idempotent Occurrence Mutations

All mutation endpoints require a `clientOperationId` (UUID) for idempotency.
If the same `clientOperationId` is sent again, the original response is returned without side effects.

### POST `/api/households/{householdId}/chores/occurrences/{occurrenceId}/complete`

**Request:**

```json
{ "clientOperationId": "11111111-1111-1111-1111-111111111111" }
```

**Response 200:** ChoreOccurrence with events.

### POST `.../occurrences/{occurrenceId}/undo`

Same request shape. Sets status back to Pending.

### POST `.../occurrences/{occurrenceId}/skip`

Same request shape. Sets status to Skipped.

### POST `.../occurrences/{occurrenceId}/reassign`

**Request:**

```json
{
  "clientOperationId": "22222222-2222-2222-2222-222222222222",
  "newAssigneeId": "bbb..."
}
```

**Response 200 (all mutations):**

```json
{
  "id": "...",
  "choreTemplateId": "ddd...",
  "choreTitle": "Do the dishes",
  "assigneeId": "aaa...",
  "assigneeName": "Alice",
  "dueDate": "2025-06-15",
  "status": 1,
  "version": 1,
  "events": [
    {
      "id": "...",
      "eventType": 1,
      "performedByUserId": "aaa...",
      "performedByName": "Alice",
      "occurredAtUtc": "...",
      "clientOperationId": "11111111-...",
      "metadata": null
    }
  ]
}
```

---

## Calendar

### GET `/api/households/{householdId}/calendar/aggregates?from=2025-06-01&to=2025-06-30&assigneeId=...`

Per-day aggregates. `assigneeId` is optional.

**Response 200:**

```json
[
  { "date": "2025-06-15", "due": 3, "done": 1, "missed": 1, "skipped": 1 },
  { "date": "2025-06-16", "due": 2, "done": 0, "missed": 0, "skipped": 0 }
]
```

### GET `/api/households/{householdId}/calendar/occurrences?from=2025-06-01&to=2025-06-30&assigneeId=...`

Full occurrence list with events for a date range.

**Response 200:** Array of ChoreOccurrence objects (same shape as mutation responses).

---

## Achievements

All achievement endpoints take an optional `scope` query parameter:
`0`/`Me`, `1`/`Partner`, `2`/`Household` (default `Household`). `Partner`
returns 404 when the household has no second member. Date and week boundaries
use the household timezone.

### GET `/api/households/{householdId}/achievements/summary?from=2025-06-01&to=2025-06-30&scope=Household`

Aggregated summary for a date range (by occurrence due date).

**Response 200:**

```json
{
  "totalCompleted": 12,
  "totalScheduled": 15,
  "completionRate": 0.8,
  "completedByDay": [{ "date": "2025-06-15", "count": 3 }],
  "topTemplates": [{ "templateId": "...", "title": "Do the dishes", "completedCount": 5 }],
  "onTimeCompleted": 10,
  "lateCompleted": 2
}
```

### GET `/api/households/{householdId}/achievements/today?scope=Me`

Today's completed chores (by completion event timestamp in the household timezone).

**Response 200:**

```json
{
  "completedCountToday": 1,
  "completions": [
    {
      "occurrenceId": "...",
      "title": "Do the dishes",
      "completedAtUtc": "...",
      "scheduledDate": "2025-06-15",
      "wasLate": false
    }
  ]
}
```

### GET `/api/households/{householdId}/achievements/streaks?scope=Household`

Completion and on-time streaks (consecutive days with at least one completion).

**Response 200:**

```json
{ "currentStreakDays": 3, "longestStreakDays": 7, "currentOnTimeStreakDays": 2 }
```

### GET `/api/households/{householdId}/achievements/badges?scope=Household`

Deterministic badges/milestones based on completion totals, streaks, and perfect weeks.

**Response 200:**

```json
{
  "earnedBadges": [{ "key": "completions_10", "title": "10 Completions", "description": "Reached 10 completions!" }],
  "progressBadges": [{ "key": "completions_25", "title": "25 Completions", "description": "Complete 25 chores", "current": 12, "target": 25 }]
}
```

---

## Recipes

All recipe endpoints are household-scoped and require the caller to be a member of the household.
Imported recipes are saved through the same create/update contract as custom recipes; the frontend should review the preview payload and then POST or PUT that reviewed recipe body.

### GET `/api/households/{householdId}/recipes?includeArchived=false`

List recipes for a household. Archived recipes are excluded by default.

**Response 200:**

```json
[
  {
    "id": "...",
    "name": "Weeknight Rice Bowl",
    "description": "Reliable family dinner",
    "originType": 0,
    "ingredientCount": 3,
    "instructionCount": 2,
    "servings": 2,
    "imageUrl": "https://cdn.example.com/dinner.jpg",
    "isArchived": false,
    "updatedAtUtc": "2026-04-29T07:45:00Z",
    "tags": ["Dinner", "Family"]
  }
]
```

### GET `/api/households/{householdId}/recipes/{recipeId}`

Get full recipe details.

**Response 200:**

```json
{
  "id": "...",
  "householdId": "...",
  "name": "Weeknight Rice Bowl",
  "description": "Reliable family dinner",
  "originType": 0,
  "source": {
    "type": 0,
    "url": null,
    "domain": null,
    "attribution": null,
    "requiresManualReview": false,
    "importWarnings": []
  },
  "ingredients": [
    {
      "id": "...",
      "sortOrder": 0,
      "rawText": "2 cups rice",
      "quantity": 2,
      "unit": "cups",
      "item": "rice",
      "note": null
    },
    {
      "id": "...",
      "sortOrder": 1,
      "rawText": "1 onion, diced",
      "quantity": 1,
      "unit": null,
      "item": "onion",
      "note": "diced"
    }
  ],
  "instructions": [
    { "id": "...", "sortOrder": 0, "text": "Cook the rice.", "section": null },
    { "id": "...", "sortOrder": 1, "text": "Saute the onion.", "section": null }
  ],
  "servings": 2,
  "yieldText": "Serves 2",
  "prepMinutes": 10,
  "cookMinutes": 20,
  "totalMinutes": 30,
  "nutrition": {
    "calories": 450,
    "proteinGrams": 12,
    "carbsGrams": 60,
    "fatGrams": 10
  },
  "imageUrl": "https://cdn.example.com/dinner.jpg",
  "tags": ["Dinner", "Family"],
  "isArchived": false,
  "createdAtUtc": "...",
  "updatedAtUtc": "..."
}
```

### POST `/api/households/{householdId}/recipes`

Create a custom recipe, or save a reviewed imported recipe.

**Request:**

```json
{
  "name": "Weeknight Rice Bowl",
  "description": "Reliable family dinner",
  "source": null,
  "ingredients": [
    {
      "rawText": "2 cups rice",
      "quantity": 2,
      "unit": "cups",
      "item": "rice",
      "note": null
    },
    {
      "rawText": "1 onion, diced",
      "quantity": 1,
      "unit": null,
      "item": "onion",
      "note": "diced"
    },
    {
      "rawText": "salt to taste",
      "quantity": null,
      "unit": null,
      "item": null,
      "note": null
    }
  ],
  "instructions": [
    { "text": "Cook the rice.", "section": null },
    { "text": "Saute the onion.", "section": null }
  ],
  "servings": 2,
  "yieldText": "Serves 2",
  "prepMinutes": 10,
  "cookMinutes": 20,
  "totalMinutes": 30,
  "nutrition": {
    "calories": 450,
    "proteinGrams": 12,
    "carbsGrams": 60,
    "fatGrams": 10
  },
  "imageUrl": "https://cdn.example.com/dinner.jpg",
  "tags": ["Dinner", "Family"],
  "isArchived": false
}
```

**Response 201:** Same shape as recipe details.

### PUT `/api/households/{householdId}/recipes/{recipeId}`

Replace a recipe with a full reviewed payload. Ingredients and instructions are replaced as a whole.

### DELETE `/api/households/{householdId}/recipes/{recipeId}`

Archive a recipe. Archived recipes are excluded from default listing and cannot be used in new meal plans.

---

## Recipe Import Preview

### POST `/api/households/{householdId}/recipes/import-preview`

Fetch a third-party recipe page server-side and extract normalized recipe data from structured metadata.

**Request:**

```json
{ "url": "https://example.com/recipe/chili" }
```

**Response 200:**

```json
{
  "sourceUrl": "https://example.com/recipe/chili",
  "sourceDomain": "example.com",
  "confidence": 2,
  "requiresManualReview": false,
  "warnings": [],
  "recipe": {
    "name": "Imported Chili",
    "description": "A structured-data recipe.",
    "source": {
      "type": 1,
      "url": "https://example.com/recipe/chili",
      "domain": "example.com",
      "attribution": "Recipe Author",
      "requiresManualReview": false,
      "importWarnings": []
    },
    "ingredients": [
      {
        "rawText": "1 cup rice",
        "quantity": 1,
        "unit": "cup",
        "item": "rice",
        "note": null
      }
    ],
    "instructions": [{ "text": "Heat the oil.", "section": null }],
    "servings": 4,
    "yieldText": "4 servings",
    "prepMinutes": 15,
    "cookMinutes": 30,
    "totalMinutes": 45,
    "nutrition": {
      "calories": 420,
      "proteinGrams": 18,
      "carbsGrams": 32,
      "fatGrams": 14
    },
    "imageUrl": "https://example.com/images/chili.jpg",
    "tags": ["Dinner", "Chili"],
    "isArchived": false
  }
}
```

`confidence`: 0=Low, 1=Medium, 2=High.

Supported import scenarios:

- Fully supported: server-reachable HTML pages that expose Schema.org `Recipe` JSON-LD with usable ingredient and instruction data, including English or Russian content with common Russian units, Russian duration text, and UTF-8 or Windows-1251 HTML.
- Partially supported: pages with JSON-LD recipe objects that are missing fields like servings, nutrition, image, or machine-readable ingredient quantities. These return `200` with `warnings` and `requiresManualReview=true`.
- Intentionally unsupported: private/local URLs, JS-only recipe pages without embedded JSON-LD, login/captcha-protected pages, blocked/rate-limited fetches, and pages that do not expose `Recipe` structured metadata.

Frontend expectation:

- Show `warnings` and respect `requiresManualReview` before saving.
- Save the reviewed preview through the standard `POST /recipes` or `PUT /recipes/{recipeId}` contract.
- Preserve `source` metadata when saving imported recipes so auditability is retained.

---

## Meal Plans

One meal-plan entry is allowed per `(householdId, date, mealSlot)` combination.

### GET `/api/households/{householdId}/meal-plans?from=2026-05-01&to=2026-05-07`

List meal-plan entries for a date range.

**Response 200:**

```json
[
  {
    "id": "...",
    "householdId": "...",
    "date": "2026-05-03",
    "mealSlot": 2,
    "recipe": {
      "recipeId": "...",
      "name": "Weeknight Rice Bowl",
      "imageUrl": "https://cdn.example.com/dinner.jpg",
      "originType": 0,
      "isArchived": false
    },
    "servingsPlanned": 4,
    "notes": "Prep ahead",
    "createdByUserId": "...",
    "updatedByUserId": "...",
    "createdAtUtc": "...",
    "updatedAtUtc": "..."
  }
]
```

`mealSlot`: 0=Breakfast, 1=Lunch, 2=Dinner, 3=Snack, 4=Other.

### POST `/api/households/{householdId}/meal-plans`

Create a meal-plan entry.

**Request:**

```json
{
  "date": "2026-05-03",
  "mealSlot": 2,
  "recipeId": "...",
  "servingsPlanned": 4,
  "notes": "Prep ahead"
}
```

**Response 201:** Same shape as meal-plan entry.

### PUT `/api/households/{householdId}/meal-plans/{entryId}`

Replace an existing meal-plan entry.

### DELETE `/api/households/{householdId}/meal-plans/{entryId}`

Delete a meal-plan entry. **Response 204.**

---

## Grocery List

### GET `/api/households/{householdId}/meal-plans/grocery-list?from=2026-05-01&to=2026-05-07`

Generate an MVP grocery list from planned meals in a date range.

**Response 200:**

```json
{
  "from": "2026-05-01",
  "to": "2026-05-07",
  "items": [
    {
      "displayText": "4 cup rice",
      "quantity": 4,
      "unit": "cup",
      "item": "rice",
      "isNormalized": true,
      "sources": [
        {
          "plannedDate": "2026-05-03",
          "mealSlot": 2,
          "recipeId": "...",
          "recipeName": "Weeknight Rice Bowl",
          "servingsMultiplier": 2
        }
      ]
    },
    {
      "displayText": "salt to taste",
      "quantity": null,
      "unit": null,
      "item": "salt to taste",
      "isNormalized": false,
      "sources": [
        {
          "plannedDate": "2026-05-03",
          "mealSlot": 2,
          "recipeId": "...",
          "recipeName": "Weeknight Rice Bowl",
          "servingsMultiplier": 2
        }
      ]
    }
  ],
  "warnings": [
    "Some ingredients could not be quantity-normalized and were preserved as human-readable lines."
  ]
}
```

Frontend expectation:

- Use `isNormalized=false` rows as display-first fallback lines instead of pretending the quantity math is exact.
- Render `warnings` when servings metadata or quantity normalization was incomplete.

---

## Devices

### POST `/api/devices`

Register a device push token.

**Request:**

```json
{ "platform": 0, "token": "fcm-token-string..." }
```

`platform`: 0=FCM, 1=APNs.

**Response 204.**

### DELETE `/api/devices`

Unregister a device push token. Same request shape. **Response 204.**

---

## Error Responses

All errors follow RFC 9457 Problem Details:

```json
{
  "status": 400,
  "title": "Bad Request",
  "detail": "Weekly rule requires at least one day of week.",
  "instance": "/api/households/.../chores/templates",
  "code": "bad_request"
}
```

| Status | When                                                                                                  |
| ------ | ----------------------------------------------------------------------------------------------------- |
| 400    | Validation / argument errors                                                                          |
| 403    | Permission denied                                                                                     |
| 404    | Entity not found                                                                                      |
| 409    | Conflict (e.g., household full)                                                                       |
| 422    | Import URL or source content is syntactically reachable but unsupported for structured recipe parsing |
| 502    | Third-party recipe fetch blocked or upstream fetch failure                                            |
| 500    | Unexpected server error                                                                               |

New recipe and meal-plan specific error codes include:

- `household_access_denied`
- `recipe_not_found`
- `recipe_archived`
- `recipe_import_fetch_blocked`
- `recipe_import_fetch_failed`
- `recipe_import_parse_failed`
- `unsupported_domain`
- `meal_plan_not_found`
- `meal_plan_slot_taken`
- `invalid_meal_slot`
