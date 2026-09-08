# Cloud Computing and DevOps Reference

## Cloud Service Models

### IaaS (Infrastructure as a Virtual machines, networks, storage. You manage: OS, runtime, apps. Provider manages: hardware, networking, virtualization.
- **Examples**: AWS EC2, Azure VMs, Google Compute Engine

### PaaS (Platform as a Service)
- Provider manages: OS, runtime, middleware, scaling
- You manage: Application code and data
- **Examples**: AWS Elastic Beanstalk, Azure App Service, Heroku, Google App Engine

### SaaS (Software as a Service)
- Provider manages everything
- You use the software as-is
- **Examples**: Gmail, Salesforce, Slack, Microsoft 365

## Major Cloud Providers

### AWS (Amazon Web Services)
- **Compute**: EC2 (VMs), Lambda (serverless), ECS/EKS (containers)
- **Storage**: S3 (objects), EBS (block), EFS (file)
- **Database**: RDS (relational), DynamoDB (NoSQL), ElastiCache (cache), Aurora (managed PostgreSQL/MySQL)
- **Networking**: VPC, CloudFront (CDN), Route 53 (DNS)
- **AI/ML**: SageMaker, Bedrock, Rekognition
- **Analytics**: Redshift (data warehouse), Athena (serverless SQL on S3)

### Microsoft Azure
- **Compute**: Virtual Machines, Azure Functions (serverless), AKS (Kubernetes)
- **Storage**: Blob Storage, Azure Files, Azure Disk
- **Database**: Azure SQL Database, Cosmos DB (multi-model), Azure Database for PostgreSQL
- **Networking**: Virtual Network, Azure Front Door, Azure DNS
- **AI/ML**: Azure OpenAI Service, Azure Machine Learning
- **Enterprise**: Active Directory, Microsoft 365 integration

### Google Cloud Platform (GCP)
- **Compute**: Compute Engine, Cloud Functions, GKE (Kubernetes)
- **Storage**: Cloud Storage, Persistent Disk
- **Database**: Cloud SQL, Firestore, BigQuery, Cloud Spanner
- **Networking**: VPC, Cloud CDN, Cloud DNS
- **AI/ML**: Vertex AI, Gemini API, BigQuery ML

## DevOps Practices

### CI/CD (Continuous Integration / Continuous Deployment)
- **CI**: Automatically build and test code on every commit
- **CD**: Automatically deploy to production after tests pass
- **Tools**: GitHub Actions, GitLab CI, Jenkins, CircleCI, Azure DevOps

### Containerization
- **Docker**: Package apps with dependencies into portable containers
  - `Dockerfile`: Build instructions
  - `docker-compose.yml`: Multi-container orchestration
  - Images vs Containers: Images are templates, containers are running instances
- **Kubernetes (K8s)**: Orchestrate containers at scale
  - Pods: smallest deployable unit (1+ containers)
  - Services: stable network endpoint for pods
  - Deployments: manage pod replicas and updates
  - ConfigMaps/Secrets: externalized configuration

### Infrastructure as Code (IaC)
- **Terraform**: Multi-cloud, declarative HCL syntax
- **AWS CloudFormation**: AWS-native, JSON/YAML templates
- **Azure Bicep**: Azure-native, simplified syntax
- **Pulumi**: General-purpose language (Python, TypeScript, Go)

### Monitoring and Observability
- **Metrics**: Quantitative measurements (CPU, memory, request latency)
- **Logs**: Timestamped event records
- **Traces**: Request path through distributed systems
- **Tools**: Prometheus + Grafana, Datadog, New Relic, Azure Monitor

## Serverless Architecture

### What is Serverless?
Run code without managing servers. Provider handles scaling, patching, and availability.

### Key Characteristics
- Pay per execution (not per server)
- Auto-scales to zero when not in use
- Event-driven triggers
- Stateless by design

### AWS Lambda
- Triggered by: API Gateway, S3 events, DynamoDB streams, SQS, EventBridge
- Max execution time: 15 minutes
- Memory: 128 MB to 10 GB (CPU scales proportionally)
- Cold start: first invocation may have latency

### Azure Functions
- Similar to Lambda with Azure ecosystem integration
- Supports Durable Functions for orchestration
- Premium plan for VNET integration and pre-warmed instances

## Microservices vs Monolith

### Monolith
- Single codebase, single deployment
- Simpler to develop, test, deploy initially
- Harder to scale individual components
- Risk: tight coupling over time

### Microservices
- Independent services communicating via APIs
- Each service owns its data and logic
- Can scale, deploy, and develop independently
- Trade-off: operational complexity, distributed system challenges

### When to Choose What
- **Start monolith**: Small team, early product, unclear requirements
- **Split when**: Team size grows, deployment bottlenecks, scaling needs differ per component
- **Don't split just because**: it's trendy or "someone said microservices"

## Cloud Cost Optimization
- Right-size instances (don't over-provision)
- Use reserved instances for predictable workloads (30-60% savings)
- Use spot instances for fault-tolerant workloads (up to 90% savings)
- Auto-scale based on actual demand
- Delete unused resources (old snapshots, idle load balancers)
- Use serverless for sporadic workloads
- Monitor with cost dashboards and alerts
