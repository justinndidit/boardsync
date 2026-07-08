# Task Board Platform: Complete Documentation

This document consolidates the Product Requirements Document (PRD) and the Phased Delivery Plan for the Task Board Platform, an Azure DevOps Boards clone built using a Modular Monolith architecture.

---

## PART 1: Product Requirements Document (PRD)

### 1. Overview

#### 1.1 Problem Statement
Teams need a self-hosted or lightweight alternative to Azure DevOps Boards for tracking work items, organizing them into sprints, and visualizing progress on a Kanban-style board. This product recreates the core "Boards" experience: work item tracking, backlogs, sprints/iterations, and Kanban boards.

#### 1.2 Goals
* **Kanban Board:** Provide a Kanban board for visualizing work item state (To Do / In Progress / Done, customizable columns).
* **Agile Sprint Planning:** Support iterations, sprint backlog, and capacity planning.
* **Work Item Hierarchy:** Support a complete work item hierarchy (Epic → Feature → User Story/PBI → Task → Bug).
* **Multi-Tenancy Structure:** Support multiple projects/teams within an organization.
* **Architecture:** Be built as a modular monolith—one deployable unit, internally separated into well-bounded modules with clear interfaces, so modules could later be extracted into services if needed.

#### 1.3 Non-Goals
The following features are explicitly out of scope for this PRD:
* Source control (Repos)
* CI/CD (Pipelines)
* Wiki
* Test case management
* Third-party marketplace/extensions

#### 1.4 Target Users
* Software teams practicing Scrum/Kanban
* Scrum Masters / Project Managers doing sprint planning
* Engineers updating task status daily
* Stakeholders viewing progress/reports

---

### 2. Architecture Approach: Modular Monolith

The system is designed as a single deployable application (single codebase, single build, single DB optionally schema-per-module) organized into independent modules.

Each module possesses:
* Its own domain models/entities
* Its own service layer (business logic)
* Its own data access layer (repository pattern), ideally its own schema/tables namespaced by module
* A public interface (module API) that other modules call (no reaching into another module's internal tables directly)
* Its own set of REST endpoints/controllers

Cross-module communication happens through in-process module interfaces (function calls / internal event bus), not HTTP, to keep it a true monolith. An internal event bus (in-process, e.g., pub/sub pattern) is recommended so modules like Notifications or Reporting can react to events (e.g., `WorkItemStateChanged`) without tight coupling.

#### 2.1 Suggested Tech Stack
* **Backend:** Node.js (NestJS, which naturally supports modular architecture via its Module system), Java/Spring Boot (Spring Modulith), or .NET (Clean Architecture + Areas)
* **Frontend:** React + TypeScript, with drag-and-drop powered by libraries like `@hello-pangea/dnd` (formerly `react-beautiful-dnd`)
* **Database:** PostgreSQL (schemas per module: `iam.`, `workitem.`, `sprint.`, etc.)
* **Cache/Queue (in-process friendly):** In-memory event emitter for MVP; Redis pub/sub if scaling later
* **Auth:** JWT-based session, with RBAC middleware

---

### 3. Module Breakdown

Each module below represents a bounded context with its own entities, services, and API surface.

#### 3.1 Identity & Access Management (IAM / Auth)
* **Purpose:** User authentication, session management, and account lifecycle.
* **Responsibilities:**
  * User registration/login (email+password; SSO optional/future)
  * Session/JWT issuance and refresh
  * Password reset
  * User profile (name, avatar, email)
* **Key Entities:** `User`, `Session`, `PasswordResetToken`
* **Core APIs:**
  * `POST /auth/register`
  * `POST /auth/login`
  * `POST /auth/refresh`
  * `POST /auth/logout`
  * `GET /users/me`
  * `PATCH /users/me`
* **Events Emitted:** `UserRegistered`, `UserLoggedIn`
* **Dependencies:** None (foundational module)

#### 3.2 Organization & Project Management
* **Purpose:** Multi-tenancy structure where Organizations contain Projects, which contain Teams.
* **Responsibilities:**
  * Create/manage Organizations
  * Create/manage Projects within an Organization
  * Create/manage Teams within a Project
  * Add/remove members to Org/Project/Team
  * Project-level settings (e.g., default area path, working days)
* **Key Entities:** `Organization`, `Project`, `Team`, `TeamMembership`, `OrganizationMembership`
* **Core APIs:**
  * `POST /orgs`, `GET /orgs/:id`
  * `POST /orgs/:id/projects`, `GET /projects/:id`
  * `POST /projects/:id/teams`
  * `POST /teams/:id/members`
* **Events Emitted:** `ProjectCreated`, `TeamCreated`, `MemberAddedToTeam`
* **Dependencies:** IAM (for user references)

#### 3.3 Work Item Management
* **Purpose:** The core CRUD and lifecycle engine for all trackable work (the heart of the system).
* **Responsibilities:**
  * Define work item types: Epic, Feature, User Story/PBI, Task, Bug
  * CRUD operations for work items (title, description, state, assignee, tags, priority, story points/effort)
  * Parent-child linking (hierarchy: Epic → Feature → Story → Task)
  * Related-item linking (e.g., "blocks", "related to")
  * State machine per work item type (e.g., New → Active → Resolved → Closed), configurable per project
  * Comments/discussion thread on a work item
  * Attachments on work items
  * History/audit trail of field changes
* **Key Entities:** `WorkItem`, `WorkItemType`, `WorkItemState`, `WorkItemLink`, `WorkItemComment`, `WorkItemHistory`, `Tag`
* **Core APIs:**
  * `POST /workitems`, `GET /workitems/:id`, `PATCH /workitems/:id`
  * `POST /workitems/:id/links`
  * `POST /workitems/:id/comments`
  * `GET /workitems/:id/history`
* **Events Emitted:** `WorkItemCreated`, `WorkItemStateChanged`, `WorkItemAssigned`, `WorkItemUpdated`
* **Dependencies:** Org & Project (work items belong to a project), IAM (assignee reference)

#### 3.4 Backlog Management
* **Purpose:** Prioritized, ordered lists of work items not yet committed to a sprint.
* **Responsibilities:**
  * Product Backlog view (all unscheduled Stories/PBIs, ranked)
  * Manual drag-to-reorder (backlog priority/rank)
  * Bulk move items into a sprint (sprint planning action)
  * Filtering backlog by area path, tag, assigned team
* **Key Entities:** `BacklogItemOrder` (tracks rank per project/team; uses `WorkItem` from the Work Item module)
* **Core APIs:**
  * `GET /projects/:id/backlog`
  * `PATCH /backlog/reorder`
  * `POST /backlog/move-to-sprint`
* **Dependencies:** Work Item Management, Sprint/Iteration Management, Org & Project

#### 3.5 Sprint / Iteration Management
* **Purpose:** Time-boxed iterations for Agile planning.
* **Responsibilities:**
  * Create/manage Iterations (Sprints) with start/end dates, per Team
  * Assign work items to a specific sprint
  * Team capacity planning (per-person hours/days available per sprint, accounting for days off)
  * Sprint burndown data source (remaining work per day)
  * Close out a sprint (move incomplete items to next sprint/backlog)
* **Key Entities:** `Iteration`, `IterationCapacity`, `TeamMemberCapacity`, `SprintWorkItemAssignment`
* **Core APIs:**
  * `POST /teams/:id/iterations`
  * `GET /iterations/:id`
  * `POST /iterations/:id/capacity`
  * `GET /iterations/:id/burndown`
  * `POST /iterations/:id/close`
* **Events Emitted:** `IterationCreated`, `IterationClosed`, `WorkItemMovedToSprint`
* **Dependencies:** Org & Project, Work Item Management

#### 3.6 Board (Kanban)
* **Purpose:** Visual drag-and-drop board representing work item state within a sprint or backlog.
* **Responsibilities:**
  * Render columns mapped to work item states (customizable columns per team, e.g., To Do / Doing / Review / Done)
  * Support swimlanes (e.g., by priority, or "Expedite" lane)
  * Drag-and-drop to change state (triggers `WorkItemStateChanged`)
  * WIP (Work-In-Progress) limits per column
  * Card customization (show assignee avatar, tags, story points)
  * Board filters (by assignee, tag, work item type)
  * Separate board views: Sprint Board (current iteration) and Backlog Board
* **Key Entities:** `BoardColumn`, `BoardColumnStateMapping`, `Swimlane`, `BoardSettings` (per team)
* **Core APIs:**
  * `GET /teams/:id/board?iteration=iterationId`
  * `PATCH /teams/:id/board/columns` (column/state transition configuration)
  * `POST /workitems/:id/move`
* **Dependencies:** Work Item Management, Sprint/Iteration Management, Org & Project

#### 3.7 Search & Filter
* **Purpose:** Cross-cutting search/query capability over work items.
* **Responsibilities:**
  * Full-text search on title/description
  * Structured query (WIQL-like: filter by state, assignee, type, tag, iteration, date range)
  * Saved queries/filters per user or shared per team
* **Key Entities:** `SavedQuery`
* **Core APIs:**
  * `GET /search?q=...`
  * `POST /queries` (save a filter)
  * `GET /queries/:id/run`
* **Dependencies:** Work Item Management (read-only queries into its data)

#### 3.8 Notifications
* **Purpose:** Keep users informed of relevant activity.
* **Responsibilities:**
  * In-app notifications (assigned to you, mentioned in comment, state changed on watched item)
  * Notification preferences per user
  * (Optional/Future) Email digest
* **Key Entities:** `Notification`, `NotificationPreference`, `Watcher` (user watching a work item)
* **Core APIs:**
  * `GET /notifications`
  * `PATCH /notifications/:id/read`
  * `POST /workitems/:id/watch`
* **Dependencies:** Listens to events from Work Item Management and Sprint modules via the event bus (no direct database writes or cross-module tight coupling).

#### 3.9 Reporting & Analytics
* **Purpose:** Dashboards and charts summarizing team progress.
* **Responsibilities:**
  * Sprint burndown chart
  * Velocity chart (story points completed per sprint, historical)
  * Cumulative flow diagram (CFD)
  * Basic project dashboard (item counts by state/type)
* **Key Entities:** Read-model/aggregation tables, e.g., `SprintSnapshot` (daily snapshot of remaining work, built by a scheduled job listening to events)
* **Core APIs:**
  * `GET /projects/:id/dashboard`
  * `GET /teams/:id/velocity`
  * `GET /iterations/:id/burndown-chart`
  * `GET /teams/:id/cfd`
* **Dependencies:** Consumes events from Work Item Management and Sprint modules (event-sourced read models); does not write to their tables.

#### 3.10 RBAC / Permissions
* **Purpose:** Cross-cutting authorization determining who can do what and where.
* **Responsibilities:**
  * Define Roles: Org Admin, Project Admin, Team Member, Reader (Stakeholder read-only)
  * Permission checks: e.g., only Project Admin can change board column config; only assignee/admin can close a work item
  * Middleware/guard used by every module's controllers
* **Key Entities:** `Role`, `Permission`, `RoleAssignment` (scoped to Org, Project, or Team)
* **Core APIs:**
  * `POST /projects/:id/roles`
  * `GET /users/:id/permissions?scope=project:123`
* **Dependencies:** IAM, Org & Project. Used as a shared library/guard by every other module. It is effectively part of the Shared Kernel from an implementation standpoint, even though it has its own data model.

---

### 4. Shared Kernel (Cross-Module Common Code)

The Shared Kernel is not a "module" with unique business logic, but rather shared utility code that every module depends on:
* Base entity classes (including common fields like `id`, `createdAt`, `updatedAt`, `createdBy`)
* Standard API response/error envelopes
* Validation utilities (DTO validation)
* The in-process event bus abstraction (`publish(event)`, `subscribe(eventType, handler)`)
* Pagination helpers
* Logging/audit utilities

---

### 5. Data Model Relationships (High-Level)

* Organization `1 — *` Project `1 — *` Team `1 — *` TeamMembership `* — 1` User
* Project `1 — *` WorkItemType (configuration)
* Project `1 — *` WorkItem `* — 1` WorkItemType
* WorkItem `* — *` WorkItem (via WorkItemLink: parent/child, related)
* Team `1 — *` Iteration
* Iteration `1 — *` WorkItem (assigned sprint)
* Team `1 — *` BoardColumn
* WorkItem `1 — *` WorkItemComment
* WorkItem `1 — *` WorkItemHistory
* User `1 — *` Notification

---

### 6. Non-Functional Requirements

| Category | Requirement |
| :--- | :--- |
| **Performance** | Board should load in `< 1s` for up to 500 work items per team view. Drag-and-drop state changes reflect in `< 500ms`. |
| **Scalability** | Modular design should allow extraction of any module into a microservice later without rewriting business logic. |
| **Availability** | Single monolith deploy; target 99.5% uptime for MVP. |
| **Security** | RBAC enforced at the API layer on every mutating endpoint; passwords hashed using strong algorithms (bcrypt/argon2); JWT short-lived + refresh tokens. |
| **Auditability** | All work item field changes must be logged in `WorkItemHistory`. |
| **Data Integrity** | Foreign keys enforced at the DB level even across module schemas (since they share one physical DB instance). |
| **Extensibility** | New work item types/states configurable per project without code changes (via a data-driven state machine). |
| **Real-time** | Board updates reflect other users' changes without a full page reload (WebSocket or polling for MVP). |

---

### 7. Success Metrics
* A team can go from backlog prioritization to sprint planning, active board tracking, and sprint close entirely within the product.
* Board drag-and-drop state changes reflect in `< 500ms`.
* Burndown chart accurately reflects daily remaining work.

---

### 8. Glossary
* **PBI:** Product Backlog Item (also known as a User Story).
* **Iteration/Sprint:** A fixed time-box (commonly 2 weeks) during which a team commits to completing a set of work items.
* **WIP Limit:** Maximum number of items allowed in a board column at once.
* **CFD:** Cumulative Flow Diagram, a chart showing work item counts per state over time.

---

## PART 2: Phased Delivery Plan (Build Phases)

### Phasing Philosophy
This phased approach focuses on **incremental delivery**. Each phase produces a usable, demoable slice. Modules are built in strict dependency order (foundational modules first). 

Because Work Item Management, Sprints, and Boards form the absolute core of "a task board with sprint planning," the phases are ordered to get a **Walking Skeleton** (`Auth` → `Project` → `Work Item` → `Board`) working as early as possible. Afterward, the plan layers in Sprints, Backlog planning tools, usability enhancements (Search, Notifications), and finally reporting capabilities.

---

### Detailed Build Phases

#### Phase 0: Foundation & Project Setup
* **Goal:** Repo, architecture skeleton, and infrastructure readiness; no user-facing features yet.
* **Scope:**
  * Set up monorepo/single-repo structure with separate module folders (`/modules/iam`, `/modules/workitem`, etc.).
  * Set up the Shared Kernel: base entity, error envelope, event bus abstraction, and logging.
  * Database setup (PostgreSQL) incorporating a schema-per-module convention and migration tooling.
  * CI basics: linter, test runner, and build pipeline.
  * Base API scaffolding (NestJS modules or equivalent) featuring a `/health` check endpoint.
  * Frontend scaffold (React + TS + routing) and the design system/component library baseline.
* **Modules Touched:** Shared Kernel only.
* **Exit Criteria:**
  * `GET /health` returns 200.
  * Empty frontend shell successfully deploys and loads.
  * A "hello module" round-trip (frontend → API → DB → response) works end-to-end.

#### Phase 1: Identity, Organizations & Projects (Walking Skeleton)
* **Goal:** Users can sign up, log in, and establish an Organization → Project → Team hierarchy.
* **Scope:**
  * **IAM Module:** User registration, login, JWT issuance, token refresh, and profile management.
  * **Org & Project Module:** Operations to create Organizations, Projects, and Teams, alongside adding members.
  * **RBAC Module (Basic):** Basic support for Org Admin / Project Admin / Member roles to gate "who can create a project."
  * **Frontend:** Implementation of login/register pages, along with Organization/Project/Team creation and switcher UI elements.
* **Modules Touched:** IAM, Org & Project, RBAC (basic).
* **Exit Criteria:**
  * A user can successfully register, log in, create an organization, create a project, create a team, and invite another user.
  * Role-based access effectively blocks non-admin users from creating a project.

#### Phase 2: Work Item Core
* **Goal:** Work items exist and can be created, edited, and viewed via simple forms and lists (no visual board yet).
* **Scope:**
  * **Work Item Management Module:** Support for work item types (Epic/Feature/Story/Task/Bug), full CRUD operations, and a state field.
  * **Fixed State Machine for MVP:** New → Active → Resolved → Closed.
  * **Work Item Fields:** Assignee, tags, description, priority, and story points.
  * **Hierarchy & Interaction:** Parent-child linking (Epic → Feature → Story → Task), comments on work items, and a history/audit trail.
  * **Frontend:** Work item list view, item detail/edit panel, and a create-work-item form.
* **Modules Touched:** Work Item Management.
* **Exit Criteria:**
  * A user can create a Story with a child Task, assign it, comment on it, and see the change properly reflected in the history.
  * The work item list can be filtered by type and state (basic filter; not utilizing a full Search module yet).

#### Phase 3: Kanban Board
* **Goal:** Establish the core visual experience through a working drag-and-drop Kanban board.
* **Scope:**
  * **Board Module:** Default board columns mapped directly to work item states, interactive drag-and-drop mechanics to change state, per-team board configuration (adding, renaming, or removing columns), WIP limits, and basic swimlanes (Default + Expedite).
  * **Card UI:** Visual layout displaying assignee avatar, tags, and story points badge.
  * **Interactivity:** Board filtering (by assignee, tag, type) and real-time synchronization (polling or WebSockets) so changes instantly reflect in separate tabs.
* **Modules Touched:** Board, Work Item Management (state transition triggers).
* **Exit Criteria:**
  * Dragging a card between columns seamlessly updates the underlying work item state.
  * Exceeding a column's WIP limit visually flags the column.
  * Two users viewing the same board see each other's live updates without a manual refresh.

#### Phase 4: Sprints & Backlog (Sprint Planning Core)
* **Goal:** Implement the defining "sprint planning" feature set that turns the platform into a true Agile management tool rather than just a basic Kanban board.
* **Scope:**
  * **Sprint/Iteration Module:** Create iterations with explicit start/end dates, assign work items to iterations, input per-person capacity, and close out sprints (moving incomplete items forward).
  * **Backlog Module:** Product Backlog view displaying ranked, unscheduled items, drag-to-reorder ranking functionality, and a bulk "move to sprint" workflow.
  * **Board Module Extensions:** Scoping the board view to a selected Iteration ("Sprint Board" view vs. general backlog board).
  * **Frontend:** Dedicated Backlog and Sprint Planning pages, featuring drag-and-drop tools to pull items into a sprint and an active capacity visualization bar per person.
* **Modules Touched:** Sprint/Iteration, Backlog, Board (extended), Work Item Management.
* **Exit Criteria:**
  * A Scrum Master can successfully plan a 2-week sprint (create iteration, set capacity, drag backlog items into the sprint).
  * The Sprint Board displays only the work items belonging to the active iteration.
  * Closing a sprint effectively shifts unfinished items back to the backlog or forward to the next sprint.

#### Phase 5: Usability Layer (Search, Filtering & Notifications)
* **Goal:** Enhance overall usability, enabling users to easily find specific information and stay informed.
* **Scope:**
  * **Search & Filter Module:** Full-text search engine, structured query builder, and saved queries capability.
  * **Notifications Module:** In-app notifications triggering on assignments, mentions, or changes to watched items, combined with watch/unwatch configurations and notification preferences.
* **Modules Touched:** Search & Filter, Notifications.
* **Exit Criteria:**
  * A user can save a structured query (e.g., "My Active Bugs") and re-run it accurately.
  * A user receives an instantaneous in-app notification when assigned a work item.

#### Phase 6: Reporting & Analytics
* **Goal:** Provide detailed, aggregate visibility into a team's progress over time.
* **Scope:**
  * **Reporting Module:** Render sprint burndown charts, velocity charts tracking historical sprints, cumulative flow diagrams (CFD), and a main project dashboard summarizing counts by state/type.
  * **Data Pipeline:** Daily background snapshot job listening to `WorkItemStateChanged` events to incrementally build reporting data.
* **Modules Touched:** Reporting (consumes events from Work Item and Sprint modules).
* **Exit Criteria:**
  * The burndown chart accurately displays remaining work per day for an active sprint.
  * The velocity chart displays completed story points across the last *N* sprints.

#### Phase 7: Hardening & Polish
* **Goal:** Maximize quality, performance, and overall production readiness.
* **Scope:**
  * Full RBAC audit pass (introducing Reader/Stakeholder roles, team-scoped permissions, and field-level restrictions if necessary).
  * Performance tuning to ensure board load times remain under target thresholds with large data volumes (500+ items).
  * Accessibility (a11y) review on the board interface, ensuring robust keyboard alternatives to drag-and-drop actions.
  * Robust error handling for edge cases such as concurrent edits, offline drag actions, and empty states.
  * Basic attachment support on work items (if omitted from Phase 2).
  * Formal load/performance testing and security reviews targeting authorization flows and RBAC bypass attempts.
* **Modules Touched:** Cross-cutting across all modules (RBAC full, performance, security).
* **Exit Criteria:**
  * All modules successfully pass the complete integrated test suite.
  * Performance targets from the PRD Non-Functional Requirements are met.
  * Formal security review sign-off is achieved.

---

### Phase Summary Table

| Phase | Focus | Key Modules Touched |
| :---: | :--- | :--- |
| **0** | Foundation | Shared Kernel |
| **1** | Identity & Structure | IAM, Org & Project, RBAC (basic) |
| **2** | Work Items | Work Item Management |
| **3** | Kanban Board | Board, Work Item Management |
| **4** | Sprint Planning | Sprint/Iteration, Backlog, Board, Work Item Management |
| **5** | Usability | Search & Filter, Notifications |
| **6** | Insights | Reporting & Analytics |
| **7** | Hardening | Cross-cutting (RBAC full, performance, security) |

> **Note:** Phases 0-4 represent the **true MVP**—a fully functioning task board featuring sprint planning workflow support. Phases 5-7 focus on enhancement and polish; these can be reprioritized or parallelized as needed once the core delivery loop (Backlog → Sprint Board → Done) is fully operational end-to-end.
