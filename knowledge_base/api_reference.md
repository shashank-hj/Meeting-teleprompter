# APIs and Web Services Reference

## What is an API?
An API (Application Programming Interface) is a contract that defines how software components communicate. It specifies the requests that can be made, how to make them, the data formats that should be used, and the conventions to follow.

## REST APIs

### Principles
- **Stateless**: Each request contains all information needed to process it
- **Client-Server**: Separation of concerns between UI and data storage
- **Cacheable**: Responses can be cached to improve performance
- **Uniform Interface**: Consistent resource identification and manipulation

### HTTP Methods
- `GET /users` — Retrieve a list of users (read-only, safe)
- `GET /users/42` — Retrieve a specific user by ID
- `POST /users` — Create a new user
- `PUT /users/42` — Replace user 42 entirely
- `PATCH /users/42` — Partially update user 42
- `DELETE /users/42` — Delete user 42

### Status Codes
- `200 OK` — Request succeeded
- `201 Created` — Resource created successfully
- `204 No Content` — Success with no response body
- `400 Bad Request` — Invalid request syntax or parameters
- `401 Unauthorized` — Authentication required
- `403 Forbidden` — Authenticated but not authorized
- `404 Not Found` — Resource doesn't exist
- `409 Conflict` — Resource state conflict (e.g., duplicate)
- `422 Unprocessable Entity` — Validation errors
- `429 Too Many Requests` — Rate limit exceeded
- `500 Internal Server Error` — Server-side failure
- `502 Bad Gateway` — Upstream service unavailable
- `503 Service Unavailable` — Server temporarily overloaded

### REST Best Practices
- Use nouns for resources: `/users`, `/orders`, `/products` (not `/getUsers`)
- Use plural nouns: `/users` not `/user`
- Version your API: `/api/v1/users`
- Support pagination: `?page=2&limit=20`
- Use proper HTTP status codes
- Return useful error messages with error codes
- Use HATEOAS for discoverability when possible

## GraphQL

### What is GraphQL?
A query language for APIs where the client specifies exactly what data it needs. Created by Facebook in 2012, open-sourced in 2015.

### Key Concepts
- **Schema**: Defines the types and relationships available
- **Query**: Read operations (equivalent to GET)
- **Mutation**: Write operations (equivalent to POST/PUT/DELETE)
- **Subscription**: Real-time updates via WebSocket

### Example Query
```graphql
query {
  user(id: 42) {
    name
    email
    posts {
      title
      comments {
        text
        author { name }
      }
    }
  }
}
```

### GraphQL vs REST
| Factor | GraphQL | REST |
|--------|---------|------|
| Data fetching | Client specifies exact fields | Server decides response shape |
| Over-fetching | No | Common problem |
| Under-fetching | No (single request) | Common (multiple endpoints) |
| Caching | Harder (POST requests) | Easy (GET is cacheable) |
| File upload | Not natively supported | Straightforward |
| Learning curve | Higher | Lower |

## gRPC

### What is gRPC?
A high-performance RPC framework by Google using Protocol Buffers (protobuf) for serialization and HTTP/2 for transport.

### Key Features
- Strongly typed contracts via `.proto` files
- Binary serialization (faster than JSON)
- Bidirectional streaming
- Built-in deadline/timeout propagation
- Deadlines and cancellation

### Use Cases
- Microservice-to-microservice communication
- High-throughput or low-latency requirements
- Streaming data (real-time feeds, chat)
- Polyglot environments (many programming languages)

## Authentication and Authorization

### API Keys
- Simple token passed in header or query parameter
- Good for: server-to-server, rate limiting
- Bad for: user authentication (not user-specific)

### OAuth 2.0
- Industry standard for delegated authorization
- Flows: Authorization Code, Client Credentials, Device Code
- Tokens: Access token (short-lived), Refresh token (long-lived)
- Scopes define granular permissions

### JWT (JSON Web Tokens)
- Self-contained tokens with encoded claims
- Structure: Header.Payload.Signature
- Claims: `sub` (subject), `exp` (expiry), `iss` (issuer), `aud` (audience)
- Stateless verification (no database lookup needed)
- Cannot be revoked before expiry (use short expiry + refresh tokens)

## API Rate Limiting
- **Token bucket**: Allows bursts up to bucket capacity
- **Sliding window**: Counts requests in a rolling time window
- **Fixed window**: Counts requests in fixed time periods (e.g., per minute)
- **Leaky bucket**: Processes requests at a fixed rate regardless of arrival rate

## Webhooks
- Server pushes events to a client URL when something happens
- Client provides a callback URL
- Server sends HTTP POST to that URL when the event occurs
- Use case: real-time notifications, integrations, CI/CD triggers
- Requires: verify signature, handle retries, respond quickly (200 OK)
