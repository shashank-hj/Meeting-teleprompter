# Meeting Best Practices and Communication Guide

## Effective Meeting Structure

### Before the Meeting
1. **Define a clear agenda** with time allocations per topic
2. **Share pre-read materials** at least 24 hours in advance
3. **Invite only necessary participants** (the "two-pizza rule")
4. **Set clear objectives**: decision, brainstorm, status update, or information sharing

### During the Meeting
1. **Start on time** and end early when possible
2. **Assign roles**: facilitator, note-taker, timekeeper
3. **Stay on topic** — use a "parking lot" for off-topic items
4. **Capture decisions and action items** in real-time
5. **Summarize key points** at the end of each agenda item

### After the Meeting
1. **Send meeting notes** within 24 hours
2. **List action items** with owners and deadlines
3. **Follow up on decisions** made during the meeting
4. **Track progress** on action items in subsequent meetings

## Communication Techniques

### Active Listening
- Give full attention to the speaker
- Paraphrase to confirm understanding: "So what you're saying is..."
- Ask clarifying questions: "Can you elaborate on...?"
- Avoid interrupting or planning your response while others speak

### Giving Feedback
- **SBI Model**: Situation-Behavior-Impact
  - "In yesterday's standup (Situation), you didn't mention the blocking issue (Behavior), which delayed our sprint by 2 days (Impact)"
- **Sandwich Method**: Positive-Constructive-Positive (use cautiously)
- Be specific, not general: "The API response time is 3 seconds" not "It's slow"

### Handling Disagreements
- Seek to understand the other perspective first
- Separate people from problems
- Focus on interests, not positions
- Look for win-win solutions
- Know when to escalate or defer the decision

## Decision-Making Frameworks

### RACI Matrix
- **R**esponsible: Does the work
- **A**ccountable: Ultimately answerable (one per task)
- **C**onsulted: Provides input before decision
- **I**nformed: Notified after decision

### RAPID Framework
- **R**ecommend: Proposes a course of action
- **A**gree: Must agree before proceeding
- **P**erform: Executes the decision
- **I**nput: Provides data and expertise
- **D**ecide: Has final authority

### DACI Framework
- **D**river: Owns the decision process
- **A**pprover: Makes the final call
- **C**ontributors: Provide input and expertise
- **I**nformed: Notified of the decision

## Sprint and Agile Meeting Types

### Daily Standup (15 min)
- What did I complete yesterday?
- What will I work on today?
- Are there any blockers?

### Sprint Planning (2-4 hours)
- Review and estimate backlog items
- Select items for the sprint
- Define sprint goal
- Break down tasks

### Sprint Review / Demo (1-2 hours)
- Demo completed work to stakeholders
- Collect feedback
- Review sprint metrics (velocity, completion rate)

### Sprint Retrospective (1-1.5 hours)
- What went well?
- What could be improved?
- What specific actions will we take?

### Backlog Refinement (1-2 hours)
- Clarify user stories
- Estimate new items
- Split large stories
- Acceptance criteria review

## Technical Discussion Tips

### When Discussing Architecture
- Start with requirements and constraints before jumping to solutions
- Use diagrams (C4 model, sequence diagrams, ERDs)
- Consider trade-offs explicitly: "Option A is faster but harder to maintain"
- Reference real-world examples and case studies
- Document decisions with context and rationale (Architecture Decision Records)

### When Discussing Code
- Focus on the code, not the person
- Use data: "This function has 80 cyclomatic complexity"
- Suggest alternatives rather than just criticizing
- Reference established patterns and style guides
- Be open to learning from others' approaches

### When Estimating Work
- Use relative estimation (story points) over absolute (hours)
- Break work into small chunks (1-3 days max)
- Identify unknowns and spikes for research
- Consider: development, testing, review, deployment
- Account for meetings, code reviews, and unexpected issues

## Key Metrics to Track

### Sprint Metrics
- **Velocity**: Story points completed per sprint
- **Sprint burndown**: Work remaining over time
- **Cycle time**: Time from start to completion of a task
- **Lead time**: Time from request to delivery

### Code Quality Metrics
- **Code coverage**: Percentage of code covered by tests
- **Bug escape rate**: Bugs found in production vs testing
- **Technical debt ratio**: Time spent on debt vs features
- **Deployment frequency**: How often code is deployed to production

### Team Health Metrics
- **Team happiness**: Regular pulse surveys
- **Meeting load**: Hours spent in meetings per week
- **Knowledge sharing**: Cross-training and documentation
- **Onboarding time**: How quickly new members become productive
