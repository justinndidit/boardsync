-- ============================================================================
-- BoardSync demo seed
--
--   docker exec -i boardsync-postgres-dev \
--     psql -U postgres -d boardsync_dev -v ON_ERROR_STOP=1 < scripts/seed_demo.sql
--
-- Builds an organization with a team, a project, four sprints and thirty work
-- items around a user who already exists. Re-runnable: it tears its own
-- organization down first, so a botched rehearsal costs one command.
--
-- ---------------------------------------------------------------------------
-- WHY THIS WRITES HISTORY AND NOT JUST STATES
--
-- Every figure in Modules/Reporting is reconstructed from work."WorkItemHistory"
-- rows where FieldName = 'State', ordered by CreatedAt. Nothing is snapshotted.
-- A seed that sets WorkItems.State and stops produces a board that looks right
-- and three reports that are empty or flat -- burndown with no curve, velocity
-- with no bars, cycle time with no median -- which is the least useful possible
-- outcome for a demo, because the reports are the part that needs real data to
-- be worth showing.
--
-- So each item gets a backdated chain of transitions, and the states below are
-- the end of those chains rather than the point of them.
--
-- ---------------------------------------------------------------------------
-- IT DOES NOT SEED A BOARD
--
-- Boards are created on demand by GetOrCreateForProjectAsync with a column per
-- state, and cards are placed by matching a column's MappedState. Seeding
-- plan."Boards" here would be a second definition of the default board, free to
-- drift from the one the API creates. Open the project and the board appears.
--
-- ---------------------------------------------------------------------------
-- IT DOES NOT CREATE A PASSWORD
--
-- It attaches to a user who has already registered -- the oldest active one --
-- and makes them the organization's admin. Two teammates are created so cards
-- have more than one name on them, and both are given a deliberately unusable
-- password hash: they exist to be assigned work, and no credential is seeded
-- that anybody could sign in with.
-- ============================================================================

DO $seed$
DECLARE
    org_slug     CONSTANT text := 'boardsync-demo';
    project_key  CONSTANT text := 'PAY';

    owner_id     uuid;
    owner_name   text;
    org_id       uuid := gen_random_uuid();
    team_id      uuid := gen_random_uuid();
    project_id   uuid := gen_random_uuid();

    mate_a       uuid := gen_random_uuid();
    mate_b       uuid := gen_random_uuid();

    assignees    uuid[];

    now_utc      timestamptz := now();

    sprint_ids   uuid[] := ARRAY[]::uuid[];
    sprint_id    uuid;

    -- One row per work item: sprint index (0 = backlog only), type, final
    -- state, title, story points.
    specs        text[][] := ARRAY[
        -- Sprint 1 -- all finished
        ['1','UserStory','Closed','Card payments accept Visa and Mastercard','5'],
        ['1','Task','Closed','Store the payment intent id on the order','3'],
        ['1','Bug','Closed','Refund of a partial capture double-counts','2'],
        ['1','Task','Closed','Retry a declined charge once, then stop','3'],
        ['1','UserStory','Closed','A failed payment tells the customer why','8'],

        -- Sprint 2
        ['2','UserStory','Closed','Download an invoice as a PDF','5'],
        ['2','Task','Closed','Invoice numbering is sequential per organization','3'],
        ['2','Task','Closed','Email the invoice when a payment settles','5'],
        ['2','Bug','Closed','Invoice totals ignore the discount line','2'],
        ['2','UserStory','Closed','Customers see every past invoice','8'],
        ['2','Task','Closed','Backfill invoice numbers for existing orders','1'],

        -- Sprint 3
        ['3','UserStory','Closed','Subscriptions renew without a card re-entry','8'],
        ['3','Task','Closed','Dunning emails on a failed renewal','5'],
        ['3','Task','Closed','Proration when a plan changes mid-cycle','5'],
        ['3','Bug','Closed','Cancelling mid-cycle charges the next period','3'],
        ['3','UserStory','Closed','A customer can change plan from the portal','5'],
        ['3','Task','Closed','Webhook for subscription.updated','2'],

        -- Sprint 4 -- the active one, mid-flight
        ['4','UserStory','Closed','Apply a promotion code at checkout','5'],
        ['4','Task','Closed','Promotion codes expire on a date','2'],
        ['4','UserStory','Resolved','Refund a payment from the order page','5'],
        ['4','Task','Resolved','Refunds appear on the invoice','3'],
        ['4','Bug','Resolved','A refunded order still counts toward revenue','3'],
        ['4','UserStory','InReview','Partial refunds, down to a line item','8'],
        ['4','Task','Active','Reconcile refunds against the payment provider',NULL],
        ['4','Task','New','Refund receipts in the customer portal','3'],

        -- Backlog -- ranked, unscheduled
        ['0','Epic','New','Multi-currency support',NULL],
        ['0','UserStory','New','Charge in the customer''s own currency','13'],
        ['0','Task','New','Daily exchange rate snapshot','5'],
        ['0','Bug','New','Rounding differs between invoice and charge','2'],
        ['0','UserStory','New','Report revenue in the organization''s currency','8']
    ];

    spec         text[];
    i            int := 0;
    item_id      uuid;
    item_number  int := 0;
    created_at   timestamptz;
    t_active     timestamptz;
    t_review     timestamptz;
    t_resolved   timestamptz;
    t_closed     timestamptz;
    sprint_ix    int;
    final_state  text;
    assignee     uuid;
    backlog_rank numeric := 0;
BEGIN
    -- ── The person this demo belongs to ─────────────────────────────────────
    SELECT "Id", "DisplayName" INTO owner_id, owner_name
    FROM public."Users"
    WHERE "IsActive"
    ORDER BY "CreatedAt"
    LIMIT 1;

    IF owner_id IS NULL THEN
        RAISE EXCEPTION
            'No active user found. Register through the app first; this seed builds an organization around a real account rather than inventing a password.';
    END IF;

    RAISE NOTICE 'Seeding for %', owner_name;

    -- ── Tear down a previous run ────────────────────────────────────────────
    -- Explicit and ordered: work."WorkItems" has no foreign key to
    -- org."Projects", so dropping the organization would leave every work item
    -- behind, unreachable and still counted by anything scanning the table.
    SELECT "Id" INTO org_id FROM org."Organizations" WHERE "Slug" = org_slug;

    IF org_id IS NOT NULL THEN
        RAISE NOTICE 'Removing the previous demo organization';

        DELETE FROM plan."SprintWorkItems" sw
        USING plan."Sprints" s
        WHERE sw."SprintId" = s."Id"
          AND s."TeamId" IN (SELECT "Id" FROM org."Teams" WHERE "OrganizationId" = org_id);

        DELETE FROM plan."Sprints"
        WHERE "TeamId" IN (SELECT "Id" FROM org."Teams" WHERE "OrganizationId" = org_id);

        DELETE FROM plan."BacklogItems"
        WHERE "ProjectId" IN (SELECT "Id" FROM org."Projects" WHERE "OrganizationId" = org_id);

        DELETE FROM plan."Boards"
        WHERE "ProjectId" IN (SELECT "Id" FROM org."Projects" WHERE "OrganizationId" = org_id);

        -- ParentId is ON DELETE RESTRICT, so break any hierarchy before the
        -- delete rather than depending on the order rows happen to come out in.
        UPDATE work."WorkItems" SET "ParentId" = NULL
        WHERE "ProjectId" IN (SELECT "Id" FROM org."Projects" WHERE "OrganizationId" = org_id);

        -- History, comments, tags and links all cascade from here.
        DELETE FROM work."WorkItems"
        WHERE "ProjectId" IN (SELECT "Id" FROM org."Projects" WHERE "OrganizationId" = org_id);

        DELETE FROM iam."RoleAssignments" WHERE "OrganizationId" = org_id;

        DELETE FROM iam."RoleAssignments"
        WHERE "TeamId" IN (SELECT "Id" FROM org."Teams" WHERE "OrganizationId" = org_id)
           OR "ProjectId" IN (SELECT "Id" FROM org."Projects" WHERE "OrganizationId" = org_id);

        -- Memberships, projects and teams cascade from the organization.
        DELETE FROM org."Organizations" WHERE "Id" = org_id;

        DELETE FROM public."Users"
        WHERE "Email" IN ('ada.demo@boardsync.local', 'grace.demo@boardsync.local');
    END IF;

    org_id := gen_random_uuid();

    -- ── Teammates ───────────────────────────────────────────────────────────
    -- 'x' is not a BCrypt hash and never verifies, so these accounts exist to
    -- hold a name on a card and cannot be signed into.
    INSERT INTO public."Users"
        ("Id","Email","FirstName","LastName","PasswordHash","ProfilePictureUrl","DisplayName",
         "IsEmailConfirmed","IsActive","IsLocked","FailedLoginAttempts","CreatedAt","UpdatedAt")
    VALUES
        (mate_a,'ada.demo@boardsync.local','Ada','Okoye','x','','Ada Okoye',
         true,true,false,0,now_utc,now_utc),
        (mate_b,'grace.demo@boardsync.local','Grace','Bello','x','','Grace Bello',
         true,true,false,0,now_utc,now_utc);

    assignees := ARRAY[owner_id, mate_a, mate_b];

    -- ── Organization, team, project ─────────────────────────────────────────
    INSERT INTO org."Organizations"
        ("Id","Slug","Name","Description","IsActive","CreatedAt","UpdatedAt","CreatedBy")
    VALUES (org_id, org_slug, 'BoardSync Demo',
            'Seeded for demonstrations. Safe to delete.', true,
            now_utc - interval '90 days', now_utc, owner_id);

    INSERT INTO org."Teams"
        ("Id","OrganizationId","Name","Description","IsActive","CreatedAt","UpdatedAt","CreatedBy")
    VALUES (team_id, org_id, 'Payments',
            'Builds and runs the payments platform.', true,
            now_utc - interval '90 days', now_utc, owner_id);

    INSERT INTO org."Projects"
        ("Id","OrganizationId","Slug","Name","Description","IsActive","CreatedAt","UpdatedAt",
         "CreatedBy","AssignedTeamId","AllowSelfCertification","Key","NextWorkItemNumber")
    VALUES (project_id, org_id, 'payments', 'Payments',
            'Charges, invoices, subscriptions and refunds.', true,
            now_utc - interval '90 days', now_utc, owner_id, team_id, false,
            project_key, array_length(specs, 1) + 1);

    -- ── Memberships and roles ───────────────────────────────────────────────
    INSERT INTO org."OrganizationMemberships"
        ("Id","OrganizationId","UserId","JoinedAt","CreatedAt","UpdatedAt","CreatedBy")
    SELECT gen_random_uuid(), org_id, u, now_utc - interval '90 days', now_utc, now_utc, owner_id
    FROM unnest(assignees) u;

    INSERT INTO org."TeamMemberships"
        ("Id","TeamId","UserId","JoinedAt","CreatedAt","UpdatedAt","CreatedBy")
    SELECT gen_random_uuid(), team_id, u, now_utc - interval '90 days', now_utc, now_utc, owner_id
    FROM unnest(assignees) u;

    INSERT INTO iam."RoleAssignments"
        ("Id","UserId","Role","Scope","OrganizationId","PrincipalType","CreatedAt","UpdatedAt","CreatedBy")
    VALUES (gen_random_uuid(), owner_id, 'OrgAdmin', 'Organization', org_id, 'User',
            now_utc, now_utc, owner_id);

    -- Ada leads and tests; Grace contributes. A Tester has to exist or nothing
    -- can leave Resolved, and the QA gate becomes a wall rather than a gate.
    INSERT INTO iam."RoleAssignments"
        ("Id","UserId","Role","Scope","TeamId","PrincipalType","CreatedAt","UpdatedAt","CreatedBy")
    VALUES
        (gen_random_uuid(), owner_id, 'TeamLead',   'Team', team_id, 'User', now_utc, now_utc, owner_id),
        (gen_random_uuid(), mate_a,   'Tester',     'Team', team_id, 'User', now_utc, now_utc, owner_id),
        (gen_random_uuid(), mate_b,   'TeamMember', 'Team', team_id, 'User', now_utc, now_utc, owner_id);

    -- ── Sprints ─────────────────────────────────────────────────────────────
    -- Three completed, so velocity has more than one bar to compare, and one
    -- in flight so the burndown has a partial curve. Burndown stops at today
    -- and is never padded forward, so a sprint that has not started shows
    -- nothing at all.
    FOR i IN 1..4 LOOP
        sprint_id := gen_random_uuid();
        sprint_ids := sprint_ids || sprint_id;

        INSERT INTO plan."Sprints"
            ("Id","Number","Goal","StartDate","EndDate","Status","TeamId","CreatedAt","UpdatedAt","CreatedBy")
        VALUES (
            sprint_id, i,
            CASE i
                WHEN 1 THEN 'Take a card payment end to end'
                WHEN 2 THEN 'Invoices customers can read and download'
                WHEN 3 THEN 'Subscriptions that renew themselves'
                ELSE 'Refunds, and getting money back out'
            END,
            -- The three completed sprints run back to back. The active one is
            -- deliberately mid-flight -- a week in, a week to go -- because a
            -- burndown stops at today and is never padded forward: a sprint
            -- ending this instant draws a full curve and reads as finished,
            -- and one starting tomorrow draws nothing at all.
            CASE WHEN i < 4
                 THEN now_utc - make_interval(days => (5 - i) * 14)
                 ELSE now_utc - interval '7 days' END,
            CASE WHEN i < 4
                 THEN now_utc - make_interval(days => (4 - i) * 14)
                 ELSE now_utc + interval '7 days' END,
            CASE WHEN i < 4 THEN 'Completed' ELSE 'Active' END,
            team_id,
            now_utc - make_interval(days => (5 - i) * 14), now_utc, owner_id);
    END LOOP;

    -- ── Work items, with the history that makes the reports real ────────────
    FOREACH spec SLICE 1 IN ARRAY specs LOOP
        i := i + 1;
        item_number := item_number + 1;
        item_id := gen_random_uuid();

        sprint_ix := spec[1]::int;
        final_state := spec[3];
        assignee := assignees[1 + (i % 3)];

        -- Created early in its sprint, or a while ago if it is only in the
        -- backlog. The offsets vary per item so the medians below are a spread
        -- rather than one number repeated thirty times.
        created_at := CASE
            WHEN sprint_ix = 0 THEN now_utc - make_interval(days => 20 + (i % 7))
            ELSE now_utc - make_interval(days => (5 - sprint_ix) * 14)
                         + make_interval(hours => 2 + (i % 5) * 6)
        END;

        INSERT INTO work."WorkItems"
            ("Id","ProjectId","TeamId","Type","State","Priority","Title","Description",
             "AssigneeId","StoryPoints","IsActive","Number","CreatedAt","UpdatedAt","CreatedBy")
        VALUES (
            item_id, project_id, team_id, spec[2], final_state,
            CASE (i % 7) WHEN 0 THEN 'Critical' WHEN 1 THEN 'High' WHEN 5 THEN 'Low' ELSE 'Medium' END,
            spec[4], NULL, assignee, spec[5]::int, true, item_number,
            created_at, now_utc, owner_id);

        -- Sprint membership, and a backlog row so rank survives a return.
        IF sprint_ix > 0 THEN
            INSERT INTO plan."SprintWorkItems"
                ("Id","SprintId","WorkItemId","Position","Rank","CreatedAt","UpdatedAt","CreatedBy")
            VALUES (gen_random_uuid(), sprint_ids[sprint_ix], item_id, item_number,
                    item_number, created_at, now_utc, owner_id);
        END IF;

        backlog_rank := backlog_rank + 1000;

        INSERT INTO plan."BacklogItems"
            ("Id","ProjectId","WorkItemId","TeamId","Rank","SprintId","CreatedAt","UpdatedAt","CreatedBy")
        VALUES (gen_random_uuid(), project_id, item_id, team_id, backlog_rank,
                CASE WHEN sprint_ix > 0 THEN sprint_ids[sprint_ix] ELSE NULL END,
                created_at, now_utc, owner_id);

        -- ── The transition chain ────────────────────────────────────────────
        -- An item that never left New writes nothing, which is what makes
        -- "committed and never started" a real figure on the overview rather
        -- than always zero.
        CONTINUE WHEN final_state = 'New';

        t_active   := created_at + make_interval(hours => 4 + (i % 9) * 4);
        t_review   := t_active   + make_interval(hours => 10 + (i % 11) * 5);
        t_resolved := t_review   + make_interval(hours => 3 + (i % 6) * 4);
        t_closed   := t_resolved + make_interval(hours => 2 + (i % 8) * 6);

        INSERT INTO work."WorkItemHistory"
            ("Id","WorkItemId","ProjectId","ChangedBy","ActorType","FieldName",
             "OldValue","NewValue","CreatedAt","UpdatedAt","CreatedBy")
        VALUES (gen_random_uuid(), item_id, project_id, assignee, 'User', 'State',
                'New','Active', t_active, t_active, assignee);

        CONTINUE WHEN final_state = 'Active';

        INSERT INTO work."WorkItemHistory"
            ("Id","WorkItemId","ProjectId","ChangedBy","ActorType","FieldName",
             "OldValue","NewValue","CreatedAt","UpdatedAt","CreatedBy")
        VALUES (gen_random_uuid(), item_id, project_id, assignee, 'Integration', 'State',
                'Active','InReview', t_review, t_review, assignee);

        CONTINUE WHEN final_state = 'InReview';

        INSERT INTO work."WorkItemHistory"
            ("Id","WorkItemId","ProjectId","ChangedBy","ActorType","FieldName",
             "OldValue","NewValue","CreatedAt","UpdatedAt","CreatedBy")
        VALUES (gen_random_uuid(), item_id, project_id, assignee, 'Integration', 'State',
                'InReview','Resolved', t_resolved, t_resolved, assignee);

        CONTINUE WHEN final_state = 'Resolved';

        -- Closed is always a person, and always the Tester. That is the whole
        -- QA gate: no integration holds workitem:verify, so nothing else can
        -- have written this row.
        INSERT INTO work."WorkItemHistory"
            ("Id","WorkItemId","ProjectId","ChangedBy","ActorType","FieldName",
             "OldValue","NewValue","CreatedAt","UpdatedAt","CreatedBy")
        VALUES (gen_random_uuid(), item_id, project_id, mate_a, 'User', 'State',
                'Resolved','Closed', t_closed, t_closed, mate_a);
    END LOOP;

    RAISE NOTICE 'Seeded % work items across 4 sprints in %/%',
        array_length(specs, 1), org_slug, project_key;
END
$seed$;
