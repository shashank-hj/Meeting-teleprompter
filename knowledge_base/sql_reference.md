# SQL Reference Guide

## What is SQL?
SQL (Structured Query Language) is a standard language for managing and querying data stored in relational databases. It allows you to create, read, update, and delete data (CRUD operations).

## Core SQL Commands

### Data Querying (SELECT)
- `SELECT * FROM employees;` — retrieves all columns from the employees table
- `SELECT name, salary FROM employees WHERE department = 'Engineering';` — filters results
- `SELECT department, COUNT(*) as headcount FROM employees GROUP BY department;` — aggregation
- `SELECT e.name, d.name FROM employees e JOIN departments d ON e.dept_id = d.id;` — combining tables

### Data Manipulation (INSERT, UPDATE, DELETE)
- `INSERT INTO employees (name, dept_id, salary) VALUES ('Alice', 3, 95000);`
- `UPDATE employees SET salary = 100000 WHERE name = 'Alice';`
- `DELETE FROM employees WHERE id = 42;`

### Schema Definition (DDL)
- `CREATE TABLE employees (id INT PRIMARY KEY, name VARCHAR(100), dept_id INT, salary DECIMAL(10,2));`
- `ALTER TABLE employees ADD COLUMN hire_date DATE;`
- `DROP TABLE employees;`

## Database Types

### Relational (SQL) Databases
- **PostgreSQL**: Advanced open-source RDBMS. Supports JSON, full-text search, CTEs, window functions. Best for complex queries and ACID compliance.
- **MySQL**: Popular open-source RDBMS. Fast for read-heavy workloads. Widely used in web applications.
- **SQLite**: Serverless, embedded database. Single-file database. Ideal for local apps, mobile apps, and prototyping. Supports FTS5 for full-text search.
- **Microsoft SQL Server**: Enterprise RDBMS with strong Windows integration, reporting services, and analytics.
- **Oracle Database**: Enterprise-grade RDBMS with advanced partitioning, clustering, and PL/SQL procedural extensions.

### NoSQL Databases
- **MongoDB**: Document store (JSON/BSON). Flexible schema. Good for rapidly changing data models.
- **Redis**: In-memory key-value store. Used for caching, session management, real-time leaderboards.
- **Cassandra**: Wide-column store. Designed for high write throughput across distributed clusters.
- **DynamoDB**: AWS managed key-value and document database. Auto-scaling, serverless.
- **Neo4j**: Graph database. Best for relationship-heavy data like social networks and knowledge graphs.

## Key Concepts

### ACID Properties
- **Atomicity**: Transactions are all-or-nothing
- **Consistency**: Database moves from one valid state to another
- **Isolation**: Concurrent transactions don't interfere
- **Durability**: Committed data survives system failures

### Indexing
- Indexes speed up data retrieval at the cost of write performance
- B-tree indexes: default, good for equality and range queries
- Hash indexes: fast equality lookups, no range support
- Composite indexes: cover multiple columns in a specific order
- Partial indexes: index only rows matching a condition

### Normalization
- **1NF**: Eliminate repeating groups, ensure atomic values
- **2NF**: Remove partial dependencies (non-key depends on part of composite key)
- **3NF**: Remove transitive dependencies (non-key depends on another non-key)
- Denormalization: intentionally add redundancy for read performance

### Query Optimization
- Use `EXPLAIN` or `EXPLAIN ANALYZE` to inspect query plans
- Avoid `SELECT *` — retrieve only needed columns
- Use appropriate indexes for WHERE, JOIN, and ORDER BY clauses
- Limit result sets with `LIMIT`/`TOP`
- Avoid functions on indexed columns in WHERE clauses (prevents index use)

## Common Patterns

### Window Functions
```sql
SELECT name, salary,
  RANK() OVER (ORDER BY salary DESC) as salary_rank,
  AVG(salary) OVER (PARTITION BY dept_id) as dept_avg
FROM employees;
```

### CTEs (Common Table Expressions)
```sql
WITH dept_stats AS (
  SELECT dept_id, AVG(salary) as avg_sal, COUNT(*) as cnt
  FROM employees GROUP BY dept_id
)
SELECT d.name, ds.avg_sal, ds.cnt
FROM departments d JOIN dept_stats ds ON d.id = ds.dept_id;
```

### Subqueries
```sql
SELECT name FROM employees
WHERE salary > (SELECT AVG(salary) FROM employees);
```

## SQL vs NoSQL Trade-offs
| Factor | SQL | NoSQL |
|--------|-----|-------|
| Schema | Rigid, predefined | Flexible, dynamic |
| Relationships | Strong (JOINs) | Weak or embedded |
| ACID | Full support | Varies (often eventual) |
| Scaling | Vertical (bigger server) | Horizontal (more servers) |
| Query language | Standardized SQL | Varies by database |
| Best for | Complex queries, transactions | Large scale, flexible data |
