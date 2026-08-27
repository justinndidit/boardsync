# Frontend deep dive — findings and what was done

A pass over the whole UI before the Intelligence work, looking for the three failure
patterns this project keeps producing rather than for a list of files to tidy.

## What was actually wrong

### 1. Dead code — 16 components, nothing imported them

An orphan sweep found 40 modules exporting something no other module imports. Sixteen were
whole components: the entire `components/analytics/` cluster, plus `AnalyticsOverview`,
`BoardTabs`, `LogoutModal`, `SprintBurnupChart`, `StatCard`, `TeamWorkload`, `SprintBoard`,
`SprintHeader`, `SprintMetrics`, `SprintNavigation`, `MemberAvatar`, `TeamSearch`,
`ActivityLoading`. All deleted.

This mattered beyond tidiness. `PriorityBreakdown` in that cluster counted High, Medium and
Low and silently dropped Critical — the highest-priority work was missing from a chart
about priority. It was dead, so nobody saw it. Dead code does not stay dead; it gets
revived by someone searching for a component name.

### 2. `Modal` — broken for every dialog in the app

The component every dialog is built on had three defects:

- **Dark-only.** `bg-slate-900`, `text-white`, no light variant, in an app that defaults to
  light. Every modal opened as a dark box.
- **No dialog semantics.** A bare `div`: no `role`, no `aria-modal`, nothing tying it to its
  title. Opening one left focus on the page behind, Tab walked out into content the overlay
  was covering, and closing dropped focus to the top of the document. For a keyboard or
  screen-reader user the dialog was, in effect, not there.
- **An unstable effect dependency** — found by the tests written for the rewrite, not by
  reading. `onClose` is passed as an inline arrow by every caller, so it is a new function
  each render; with it in the dependency array the effect tore down and rebuilt on every
  parent render, and its cleanup restores focus to the trigger. Typing in a dialog whose
  parent re-rendered kicked focus out to the page behind, mid-keystroke.

Rewritten with a focus trap, focus restore, scroll lock, Escape, and `aria-labelledby`.
Focus lands on the first control in the *content*, not the close button that happens to come
first in DOM order.

Eight tests now cover the parts that a typecheck cannot see: where focus goes on open, that
Tab cannot leave, that Shift+Tab wraps, that focus returns to the trigger, and that the
scroll lock is released. The previous version compiled cleanly and failed every one.

### 3. Four different ways of saying "loading"

Seven files defined their own loading, empty and error states. The app said the same thing
four ways, and — worse — an empty result and a failed request often looked identical, which
matters because one means "nothing to show" and the other means "we do not know".

The `ReportState` components built for the reports page were already the right shape, so they
were promoted to `components/shared/AsyncState.tsx` rather than copied: `Panel`, `Loading`,
`ErrorState`, `EmptyState`, `InlineSkeleton`. Reports now delegates to them.

Loading is a skeleton rather than the word "Loading", so the page does not jump when data
lands, with an `sr-only` label because a skeleton says nothing to a screen reader. Errors
carry `role="alert"` and a retry. Breadcrumbs were rendering the literal string "Loading..."
where an organization or project name goes, which reads as a page actually called that.

Adopted in `ProjectRepositoriesPage`, `ProjectPeoplePage`, `TeamRolesPanel`,
`OrganizationHeader` and `BoardHeader`. Two other spots were left alone: a spinner on a
"Load more" button and a small status label are already idiomatic, and changing them would
have been churn.

## What was checked and found fine

A first sweep for dark-mode gaps flagged sixteen files. Thirteen were false positives:
`AuthLayout` forces light mode deliberately, and `SprintCard` and `CreateSprintModal` theme
through an `isDark` hook rather than Tailwind variants. Only `Modal` and `MetricCard` were
genuinely broken. Worth recording, because the same heuristic will over-report again.

## What is still open

**46 lint errors, and they are one problem.** Roughly 26 are `react-hooks/set-state-in-effect`
across 36 hand-rolled fetch hooks that each follow the same shape: `useEffect` → `setLoading`
→ `await` → `setData`. There is no caching, no dedup and no cancellation, so navigating
between pages refetches everything already held, and a slow response can land after the user
has moved on.

Fixing this properly means adopting a query library (TanStack Query is the obvious choice),
which would delete most of those 36 hooks. That is a dependency decision and a sizeable
change, so it is being raised rather than made. The alternative — hand-rolling cancellation
and a cache into each hook — is more code and less correct.

**Five files over 1000 lines**: `BoardsPage` 2230, `WorkItemDrawer` 2003, `SprintPage` 1769,
`WorkItemsPage` 1138, `OverviewPage` 1101. Not urgent, but `BoardsPage` and `WorkItemDrawer`
are where new board behaviour lands, so they are the ones that will hurt.

**`PermissionGate` is dead.** Nothing imports it; gating happens ad-hoc through `useCan`.
Either adopt it or delete it — a permission component nobody uses is a trap, because the next
person to find it will assume it is the sanctioned path.

**No render tests outside `Modal`.** The eight added here are the first component tests in the
suite; the other 73 are hooks and services.
