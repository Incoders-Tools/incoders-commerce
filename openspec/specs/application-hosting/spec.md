# Application Hosting Specification

## Purpose

Define runnable process hosts for the three walking-skeleton entry points — Commerce.Cloud.Api, Commerce.Web, Commerce.Pos.Windows — turning existing class libraries into launchable, health-checkable products without altering their domain logic.

## Requirements

### Requirement: Cloud API Runnable Host

Commerce.Cloud.Api MUST be an `Microsoft.NET.Sdk.Web` host with a `Program.cs` entry point exposing existing services over HTTP, and MUST expose a health endpoint reporting real dependency status (including database connectivity). Kestrel MUST bind to `0.0.0.0` and an environment-provided port.

#### Scenario: Local start with real database

- GIVEN Commerce.Cloud.Api is started with a valid Postgres connection string
- WHEN `dotnet run` completes startup
- THEN the process listens on `0.0.0.0` and the health endpoint reports the database as reachable

#### Scenario: Health endpoint reflects a broken dependency

- GIVEN the configured database is unreachable
- WHEN the health endpoint is queried
- THEN it reports an unhealthy status without crashing the process

### Requirement: Web SPA Consumes Real HTTP Endpoints

Commerce.Web MUST be a React + TypeScript + Tailwind CSS + shadcn/ui single-page application that calls Commerce.Cloud.Api's real HTTP endpoints; it MUST NOT depend on mocked or hardcoded data for its primary flows.

#### Scenario: Catalog/order flow against a live API

- GIVEN Commerce.Cloud.Api is running and reachable
- WHEN a user loads Commerce.Web and performs a catalog/order action
- THEN the SPA renders data returned by the live API response

#### Scenario: API unreachable

- GIVEN Commerce.Cloud.Api is unreachable
- WHEN Commerce.Web attempts a request
- THEN the SPA surfaces a visible error state instead of silently failing or showing stale mock data

### Requirement: POS Shell with In-Process Branch Node

Commerce.Pos.Windows MUST be a real WPF (`Microsoft.NET.Sdk.Wpf`) application shell with an `App.xaml` entry point, and MUST host Commerce.BranchNode in-process within the same WPF process (not as a separate service). It MUST run unsigned for local validation.

#### Scenario: POS launches with embedded branch node

- GIVEN Commerce.Pos.Windows is launched on a Windows machine or VM
- WHEN the application starts
- THEN the WPF shell renders and Commerce.BranchNode initializes within the same process

#### Scenario: POS reaches configured Cloud.Api target

- GIVEN Commerce.Pos.Windows is configured to point at either a local or a deployed staging Cloud.Api
- WHEN the POS performs a synchronization-dependent action
- THEN it communicates with the configured target without requiring code changes to switch targets

### Requirement: Additive Hosting Without Domain Regression

Adding host entry points MUST NOT modify existing domain logic in commerce-foundation's class libraries, and all existing automated tests MUST continue to pass unchanged.

#### Scenario: Existing test suite remains green

- GIVEN the hosting layer is added to Cloud.Api, Web, and Pos.Windows
- WHEN the existing test suite runs
- THEN every previously passing test still passes with no modification to its assertions
