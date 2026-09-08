# Web Development and Software Architecture Reference

## Frontend Technologies

### HTML/CSS/JavaScript (The Foundation)
- **HTML**: Structure and semantics of web pages
- **CSS**: Styling, layout (Flexbox, Grid), responsive design
- **JavaScript**: Client-side behavior, DOM manipulation, async operations

### Modern JavaScript Frameworks
- **React**: Component-based UI library by Facebook. Virtual DOM, JSX syntax, hooks for state management. Largest ecosystem.
- **Vue.js**: Progressive framework. Templates or JSX, Composition API, easy learning curve.
- **Angular**: Full framework by Google. TypeScript-first, dependency injection, RxJS for observables. Enterprise-oriented.
- **Svelte**: Compile-time framework. No virtual DOM, true reactivity, smaller bundle sizes.

### State Management
- **Redux**: Predictable state container. Single source of truth, pure functions, immutability.
- **Zustand**: Minimal state management. Simple API, no boilerplate.
- **Pinia/Vuex**: Vue-specific state management.
- **React Context**: Built-in state sharing (good for small apps, not for complex state).

## Backend Technologies

### Node.js / Express
- JavaScript runtime for server-side code
- Express: minimal web framework for Node.js
- Non-blocking I/O, event-driven architecture
- NPM ecosystem: largest package registry

### Python / Django / Flask
- Django: full-featured framework (ORM, admin, auth, templates)
- Flask: lightweight micro-framework (you choose components)
- FastAPI: modern, high-performance, async, automatic API docs

### Go / Gin / Echo
- Compiled language with goroutines for concurrency
- Gin/Echo: fast HTTP frameworks
- Excellent for microservices, CLI tools, infrastructure

### Java / Spring Boot
- Enterprise-grade framework
- Dependency injection, AOP, transaction management
- Massive ecosystem, strong typing, mature tooling

## Software Architecture Patterns

### MVC (Model-View-Controller)
- **Model**: Data and business logic
- **View**: User interface
- **Controller**: Handles input, orchestrates Model and View
- Used by: Django, Spring, Rails, ASP.NET

### Microservices Architecture
- Independent services, each with its own database
- Communicate via REST, gRPC, or message queues
- Benefits: independent scaling, deployment, technology diversity
- Challenges: data consistency, service discovery, distributed tracing

### Event-Driven Architecture
- Components communicate through events
- **Event sourcing**: Store events instead of current state
- **CQRS**: Separate read and write models
- Tools: Apache Kafka, RabbitMQ, AWS EventBridge, Azure Service Bus

### Clean Architecture / Hexagonal
- Core business logic at center
- Dependencies point inward only
- Adapters for external systems (DB, UI, APIs)
- Easier to test and swap implementations

## Database Design Patterns

### Data Access Patterns
- **Repository Pattern**: Abstracts data access behind an interface
- **Unit of Work**: Groups multiple operations into a single transaction
- **Active Record**: Object that knows how to persist itself (Django ORM, Rails ActiveRecord)
- **Data Mapper**: Separates domain objects from persistence logic (Hibernate, SQLAlchemy)

### Caching Strategies
- **Cache-aside**: Application checks cache first, then DB
- **Write-through**: Write to cache and DB simultaneously
- **Write-behind**: Write to cache, async write to DB
- **Read-through**: Cache loads from DB on miss
- **Cache invalidation**: The hardest problem in CS — expire or invalidate stale data

### Database Replication
- **Primary-Replica**: One primary handles writes, replicas handle reads
- **Multi-Primary**: Multiple nodes can accept writes (conflict resolution needed)
- **Sharding**: Split data across multiple databases by key (horizontal partitioning)

## Software Development Practices

### Agile Methodology
- **Scrum**: Sprints (1-4 weeks), daily standups, retrospectives
- **Kanban**: Visualize workflow, limit work in progress, continuous flow
- **User Stories**: "As a [user], I want [feature] so that [benefit]"
- **Definition of Done**: Shared criteria for when work is complete

### Code Quality
- **Code Reviews**: Peer review before merging
- **TDD**: Write tests first, then implementation
- **Linting**: Automated code style enforcement (ESLint, Pylint, RuboCop)
- **Type Safety**: TypeScript, Python type hints, Java generics

### Testing Pyramid
- **Unit Tests**: Fast, isolated, many (70%)
- **Integration Tests**: Test component interaction (20%)
- **End-to-End Tests**: Test full user flows (10%)
- **Smoke Tests**: Quick sanity checks after deployment

### Git Workflow
- **Feature branches**: Work on features in isolated branches
- **Pull/Merge Requests**: Code review before merging to main
- **Conventional Commits**: `feat:`, `fix:`, `docs:`, `chore:`
- **Git Flow**: main, develop, feature, release, hotfix branches
- **Trunk-based development**: Small commits directly to main with feature flags

## Security Basics

### OWASP Top 10
1. **Injection**: SQL injection, command injection, XSS
2. **Broken Authentication**: Weak passwords, session management flaws
3. **Sensitive Data Exposure**: Unencrypted data, logging secrets
4. **XML External Entities (XXE)**: XML parsing vulnerabilities
5. **Broken Access Control**: Missing authorization checks
6. **Security Misconfiguration**: Default credentials, unnecessary features
7. **Cross-Site Scripting (XSS)**: Injecting malicious scripts
8. **Insecure Deserialization**: Untrusted data deserialization
9. **Using Components with Known Vulnerabilities**: Outdated libraries
10. **Insufficient Logging**: Not monitoring or logging security events

### Security Best Practices
- Use HTTPS everywhere
- Hash passwords with bcrypt/argon2 (never MD5/SHA1)
- Validate and sanitize all user input
- Use parameterized queries (prevent SQL injection)
- Apply principle of least privilege
- Keep dependencies updated
- Use Content Security Policy (CSP) headers
- Implement rate limiting
