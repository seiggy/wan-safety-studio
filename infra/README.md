# Regional durable foundation (P3)

This root manages **durable services only**. `module.region` creates one private
workspace/storage/ACR/vault/identity/endpoint foundation for each configured region.
One optional shared App Service stays at its existing root addresses in
`control_region`. Terraform does not own compute, NAT, public IP or NAT associations.

**Terraform creates infrastructure only.** It emits no machine-readable contract (no
receipt) and sets no studio application settings on the site. The application is
configured by the operator as described in [configuration](../docs/configuration.md).

This is an in-progress cutover branch: P4's [protected private pipelines](../docs/operations/private-pipelines.md)
are defined but unexecuted; P5's hosted operational lifecycle is still pending. Do not deploy the new root
against existing state without the reviewed verified-OFF migration below. No live
deployment or state operation was performed while implementing P3.

## Inputs

`terraform.tfvars.json.example` uses synthetic IDs and `eastus2` as a **labeled
example**, not a region requirement. Replace every sample value. Add N map entries
using actual canonical Azure region names; there is no fixed one/two-region limit.

| Root input | Purpose |
| --- | --- |
| `deployment_name`, `tenant_id`, `subscription_id` | Stable customer scope; canonical lowercase UUIDs |
| `operator_principal_id` | Explicit operator object ID; lifecycle/recovery access, not a creator's browser identity |
| `publisher_principal_id` | Optional explicit publishing identity; null grants no new publication permissions |
| `control_region` | Region for the shared host and primary control storage; must be a configured key |
| `legacy_region` | Null for new deployments. Set to the one original region only for the verified-OFF migration; it must remain `control_region` |
| `regions` | Regional landing-zone references and runtime policy, below |
| `portal` | Optional supplied App Service integration subnet/DNS zone and SKU; one shared host |
| `agent`, `key_vault_id` | Optional role grants to the host identity on an existing helper resource / vault; no secret values in Terraform |

Each region requires dedicated `gpu_subnet_id`, exact canonical `gpu_subnet_cidr`,
`gpu_subnet_dedicated=true`, a distinct `private_endpoint_subnet_id`, six supplied
`private_dns_zone_ids` and a supplied `log_analytics_workspace_id`.
`existing_gpu_nsg_id` is null for an owned NSG, or the exact already-attached
customer NSG. Customer VNets/subnets/DNS links/zones/logging/NSG policy are never
created, adopted or replaced. The GPU subnet has explicit default outbound disabled
and no delegation; unrelated NSG/NAT associations fail closed.

| Regional policy | Default / limits |
| --- | --- |
| `max_nodes` | 1, positive whole deployment ceiling; not a request to create warm nodes |
| `max_job_minutes` | 120, whole minutes 1-120 |
| `max_payg_hourly_usd` | 4, finite positive retail admission threshold, not a billing cap |
| `manage_compute_egress` | False; explicit consent and exact dedicated-subnet write permission if true |
| `egress_public_ip_tags` | `{}`; obtain required policy values from the customer network owner |
| `compute_enabled` | False only. True is rejected; the field cannot allocate compute |

The output fixes the operational contract to Linux Spot/LowPriority
`Standard_NC24ads_A100_v4`, min zero, idle 120 seconds and exactly one instance per
job. There is no dedicated/larger-SKU/serverless/public-node fallback. Private
inter-region connectivity and DNS from the shared host/build runners are customer
prerequisites, not resources silently added to this root.

## Outputs and boundaries

- `regions` maps each region to informational resource IDs, names and effective ceilings
  (workspace, compute and identity IDs, storage, registry, key vault, NSG, containers,
  datastores, ownership tags and runtime policy). Compute/egress IDs are prospective;
  they do not prove existence.
- `portal` reports the shared site's ID, host names and identity IDs.
- `gpu_nsg_rules` is a region-keyed security-team handoff, not proof of effective policy.

All outputs are informational, for an operator reading `terraform output`. None is a
machine contract, and nothing in this repository parses them.

### Values to copy into the application settings

Terraform does not set application settings. Read `terraform output -json regions`
(per region) and `terraform output portal`, then copy the values you need by hand into
the Studio settings. The setting names and shapes are in
[docs\configuration.md](../docs/configuration.md). The host's `app_settings` are
ignored by Terraform (`ignore_changes`), so settings you set are never overwritten or
removed by a later apply. Terraform sets only the App Service platform settings
`WEBSITES_PORT` and `WEBSITES_ENABLE_APP_SERVICE_STORAGE`. The workspace identity
and key vault IDs are not host settings; the publish pipelines read them from the
pipeline-only `Pipeline:Regions:<region>:{WorkspaceIdentityId, KeyVaultId}`.

Run data lives in `wan-studio` / `wan_blob`, immutable releases in `wan-release` /
`wan_release`, and operational state in `wan-control` (not an AML datastore).
GPU blob writes are narrowed from the old account-wide grant to `wan-studio`;
GPU release access is read-only and no GPU role reaches control storage.
The trusted workspace service identity retains its baseline account role for AML
system datastores; it is not the identity assigned to the GPU node.

The publisher gets explicit release-container writes, ACR push, a registry
resource read (AcrPush alone cannot read the registry, and the pipeline verifies it
is private and owned), workspace environment/model registration and one
owned-account storage metadata read,
never compute or job creation. Hosted release
verification gains release **reader**, not writer. Operator/host lifecycle grants
cover explicit workspaces, compute identities, VNet/subnet read/join, and read-only
subscription SKU/quota eligibility. NAT/PIP and dedicated-subnet write permissions
exist only with managed-egress consent. No runtime role can change customer DNS,
logging, NSGs or role assignments.

Azure role actions/scopes were checked against the pinned schemas and official
references. These are scoped permission definitions, not evidence that customer
tenant policy/RBAC/networking has been live-validated. See
[ownership and permission evidence](../docs/operations/ownership-and-permissions.md).

The old shared host name, identity, plan and custom submitter UUID remain unchanged
for a legacy migration. New host role names include the workspace identity to avoid
tenant-wide name collisions. Terraform sets no application settings on the host.

## Verified-OFF address migration

Do this only in a separately approved maintenance window. Nothing below was run
against customer resources/state during P3.

1. Preserve the old configuration and cached v1 receipt outside Git. Stop the
   old submissions/compute using the previously supported code and verified native
   `underlyingResourceAction=Delete` release path. Close local submission processes
   and stop the shared App Service worker if present. A disarmed app setting is
   not proof that an old process has stopped.
2. Save a **restorable** backup with `terraform state pull` from the intended
   backend. Never substitute `terraform show -json` or a plan. Keep backups private,
   outside Git; the assessor reads the backup but never creates or pushes one.
3. Prepare a new explicit regional configuration preserving legacy region, names,
   subnets, NSG choice, DNS zones, logging, portal SKU/integration and external
   helper/vault scopes. Do not overwrite the only old config needed for emergency Stop.
4. Inspect the static assessment without Azure access:

```powershell
python infra\migration.py --config '<new-regional-config.json>' `
  --receipt '<cached-v1-foundation.json>' --state-backup '<private-backup.tfstate>'
```

This emits proposed moves, shared-host retention, rollback mapping and the
intentional GPU-role narrowing. It always emits `commands=[]` and
`verifiedOff=false` without live verification. Do not interpret a report as approval.

5. Before a real migration, remove the old automatic `.tfvars`/`.auto.tfvars`
   input from the active root **by preserving it outside `infra`**, not deleting it.
   Use only the selected new JSON file for Terraform. P3 has not moved/edited your
   real input file; hidden automatic overrides cause live assessment to fail.
6. If explicitly approved, add `--verify-live`. The tool uses the already-selected
   Azure CLI tenant/subscription; it never switches identity. Native GETs require
   exact group/workspace/host ownership, stopped host, compute/NAT/PIP absent,
   detached expected private subnet, and fully paged terminal job inventory.
   The local cached `armed.json` must be absent. Errors/unknown state emit no commands.
7. Review the emitted **exact-address** `state mv` and verified-absent `state rm`
   commands against the same backed-up backend lineage/serial immediately before
   use. The tool does not execute them. Never reuse a stale assessment or operate
   a different backend. No wildcard removal, blind import or `removed { destroy=false }`.
8. Map durable objects to `module.region["<legacy-region>"]`. Preserve shared-host
   root addresses and unchanged scoped role UUIDs. Terraform moved addresses are
   static: no variable-interpolated `moved` block is generated.
9. Review a fresh saved plan with the explicit new config and verified OFF state.
   RG, workspace, storage, vault, ACR, identities, endpoints and the shared host
   must not be replaced/deleted. The intended GPU blob role changes scope/UUID,
   removing account-wide access before runtime ownership. Region tags, control
   containers and explicit scoped grants are expected additions/updates.
   Pass the exact `terraform show -json` output through the assessor's
   `--plan-json '<saved-plan.json>'` gate. It rejects unmoved root deletes, durable
   replacements, forbidden owners and any role deletion other than the exact
   old/new GPU scope/principal/role/UUID replacement. This is a migration-safety
   gate, not IAM approval or authorization to apply. P4 must apply the same reviewed
   binary plan and recheck backend lineage/serial/fences.
10. Apply only after separate deployment approval, configure the application
    settings by hand and deploy the later compatible server. Do not Start GPU from this phase.

AzureRM role definition state IDs use `roleDefinitionResourceId|scope`; NAT/PIP
association state IDs use `natId|pipId`, and subnet associations use the subnet ID.
The assessor checks these provider-specific identities rather than issuing ARM
deletes against a composite/association ID. Customer NSGs/associations are never moved
into ownership. Deposed, unknown, duplicated or mismatched state entries require review.

Legacy infrastructure plans also use native read-only ARM list gates requiring
compute/owned egress absent and relevant jobs terminal. Missing/malformed or
paginated results fail closed; these checks do not freeze Azure between plan/apply.
Keep the maintenance fence in place and revalidate before applying.

### Rollback

Before any apply, the report's inverse durable-address mapping can be reviewed while
verified OFF. Compare current backend lineage/serial with the backup and preserve
all original IDs. Never roll back by recreating compute, forgetting live resources
or pushing an unreviewed state file. After permission/container/server changes,
use a compatible reviewed configuration/image; an old tag is not proof of
compatibility. Keep the independent release procedure available throughout cutover.

## Transitional commands

`scripts\Invoke-WanSafetyStudio.ps1` is frozen: since P9 it accepts only Status, Stop and Check, and refuses
Terraform reconciliation against this new root. Status/cached legacy Stop remain available for the pre-migration
deployment: Stop still attempts native exact-owned release, but automatic old-address
state reconciliation deliberately fails with migration guidance. It must not forget
new module addresses or claim the handoff is complete. After runtime ownership changes,
use the recovery implementation (`scripts\recovery`), not a v1 receipt. Delete the legacy script and `scripts\Test-LegacyLifecycle.ps1` together once the migration is verified.

Existing customer config/state/cache files were left untouched.

## Offline verification

Use a clean source fixture with no customer tfvars or state. The committed
`Export-MockedContracts.ps1` creates one automatically, reuses cached providers where
available, runs `fmt -check`, `validate` and the native mocked `terraform test`, and inspects the plans:

```powershell
pwsh -NoProfile -File infra\tests\Export-MockedContracts.ps1
python -m unittest discover -s infra -p 'test_*.py' -v
dotnet test tests\VideoSafetyStudio.Tests --artifacts-path .artifacts --no-restore
```

Frozen v1 OFF/ON/customer-NSG receipts remain only for the one-time v1 to v2 state
migration (`migration.py`) and its tests; they are not runtime configuration.
Native plan inspection rejects compute/egress/VNet/subnet/DNS/logging owners, any
`studio` output and any studio application setting on the site.

For a manual fixture, copy only `.tf` files, the provider lock, `modules\` and `tests\`
into a fresh directory; run `init -backend=false`, `fmt -check -recursive`, `validate`
and `test` there. Never run tests from a directory automatically loading customer
tfvars. No Azure credentials, real resources, GPU, model downloads or state mutations
are needed by the mocks. A zero-test/unknown-filter result is not a successful suite.

P3's validation is offline/mocked on Windows. Live no-replacement migration,
role-assignment sufficiency, private inter-region DNS/routing and resource deployment
remain unverified and require separately approved acceptance.
