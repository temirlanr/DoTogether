# DoTogether — API Specification

Base URL: `http://localhost:8080/api`

All endpoints except Auth require `Authorization: Bearer <access_token>`.

---

## Auth

### POST `/api/auth/magic-link`
Request a magic-link code.

**Request:**
```json
{ "email": "alice@example.com" }
```
**Response 200:**
```json
{
  "code": "482910",
  "expiresUtc": "2025-06-15T12:10:00Z",
  "message": "In production this code is sent via email."
}
```

### POST `/api/auth/verify`
Verify the code and receive tokens.

**Request:**
```json
{ "email": "alice@example.com", "code": "482910" }
```
**Response 200:**
```json
{
  "accessToken": "eyJ...",
  "refreshToken": "base64...",
  "expiresAtUtc": "2025-07-15T12:00:00Z",
  "user": { "id": "aaa...", "email": "alice@example.com", "displayName": "Alice" }
}
```

### POST `/api/auth/refresh`
**Request:**
```json
{ "refreshToken": "base64..." }
```
**Response 200:** Same shape as verify.

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
    { "userId": "aaa...", "displayName": "Alice", "email": "alice@example.com", "role": 0, "joinedAtUtc": "..." }
  ]
}
```

### GET `/api/households`
List households for the current user.

**Response 200:** Array of household objects.

### GET `/api/households/{householdId}`
Get a single household.

### POST `/api/households/{householdId}/invites`
Invite a member (Admin only).

**Request:**
```json
{ "email": "bob@example.com" }
```
**Response 201:**
```json
{ "inviteId": "...", "token": "abc123...", "expiresAtUtc": "..." }
```

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
  "recurrenceRule": { "type": 1, "interval": 1, "daysOfWeek": [], "dayOfMonth": null },
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
  "instance": "/api/households/.../chores/templates"
}
```

| Status | When |
|---|---|
| 400 | Validation / argument errors |
| 403 | Permission denied |
| 404 | Entity not found |
| 409 | Conflict (e.g., household full) |
| 500 | Unexpected server error |
