# Operational ownership and permissions

This records the approved contract and P3's implemented regional/migration boundary.
It is not an executed migration or evidence that hosted lifecycle permissions work
in a customer's tenant. See [the operational runbook](../../infra/README.md) and
the read-only `infra\migration.py` assessor.

## One owner for each resource

| Resource | Owner after cutover |
| --- | --- |
| Existing VNets/subnets, DNS zones/links, Log Analytics and customer NSG/rules | Customer landing zone; read/join only except explicit dedicated-subnet NAT consent |
| Studio RG, AML workspace/datastores, storage/containers, ACR, vault, insights, UAMIs, PEs, owned NSG and scoped RBAC | Terraform |
| One shared App Service host, its plan, identity, private endpoint and deployment configuration | Terraform/deployment pipeline |
| Ephemeral AML compute, optional owned NAT/PIP and dedicated GPU subnet NAT association | Hosted studio lifecycle, after verified-OFF handoff |
| Immutable images/code/models/licences and regional release registrations | Build/publication pipeline |
| Submission fences, operational versions, audit/attempt records | Authorized .NET operator/coordinator; independently readable by recovery |

App Service never runs Terraform or builds artifacts. The publication identity cannot
make an incomplete upload look prepared: it publishes the regional binding only after
registration/digest/blob verification. Creator endpoints cannot change operational
policy. GPU identities get run-data access and release reads, not control-state writes.

## Application authorization

| Session roles | Creator surface | Administration surface |
| --- | --- | --- |
| No approved role / anonymous | No paid generation; only the intended public health/sign-in/static surface | Denied |
| `VideoCreator` only | Existing creator actions and read-only environment information | Denied |
| `StudioAdmin` only | Does not automatically acquire paid creator permissions | Explicit approved lifecycle/release/settings actions, audited and spend-confirmed where applicable |
| Both roles | Creator permissions | Administrator permissions |

Authentication/session creation must retain these distinctions; hiding navigation
is not enforcement. Development `Auth:Mode=None` does not implicitly grant
administrator authority. Use explicit authenticated operator access for hosted
administration; keep local ComfyUI development independent.

`StudioAdmin` is enforced server-side on every `/api/studio/*` route, and every other
route needs `VideoCreator`. Sign-in accepts either role and the session carries exactly the
roles held, so a `StudioAdmin`-only session reaches only Administration (plus its content-free
change notices) and holds no creator access.

Application roles do not grant Azure RBAC, and Azure RBAC does not grant an
application role. The host uses its explicitly configured managed identity for
permitted resource operations and records the signed-in actor in its audit.
The publisher/deployer/recovery principals remain separate trust boundaries.

The hosted lifecycle (`docs\operations\hosted-lifecycle.md`) needs these host-identity grants,
all defined in `infra\operations-rbac.tf`: compute read/write/delete and job read/cancel on each
workspace; compute-identity read/assign; VNet read/join and subnet read/join (write only with
managed egress); NAT/PIP read/write/delete in the owned resource group (managed egress only); subscription
usage and SKU reads; environment/model reads and release-container read; and `wan-control` Blob
Data Contributor. It receives no workspace write, DNS, logging, customer-NSG, role-assignment or
release-write permission. Live sufficiency is unverified.

## Permission evidence

The following operation names were verified against read-only Azure provider operation
metadata on 2026-10-21. No customer resources or role assignments were mutated.
Provider metadata establishes the action name, not that a proposed role assignment
is sufficient in a particular policy/tenant/network.

| Principal/purpose | Narrow action family and scope |
| --- | --- |
| Hosted job submission | Explicit workspace `read`; job `read`, `write`, `cancel/action`; compute/approved asset metadata reads at configured workspace scopes |
| Hosted compute lifecycle | `Microsoft.MachineLearningServices/workspaces/computes/read`, `write`, `delete` at configured workspace scopes; no workspace deletion or datastore/release mutation |
| Price/quota/SKU eligibility | Public Retail Prices API; `Microsoft.MachineLearningServices/locations/usages/read` and `Microsoft.Compute/skus/read` as narrowly scoped subscription-level read grants |
| Attach the approved GPU identity | `Microsoft.ManagedIdentity/userAssignedIdentities/assign/action` and required read access on that exact pre-created identity; never identity creation or role-assignment writes |
| Deploy compute in the customer VNet | Documented VNet read/join and dedicated GPU subnet read/join; no VNet/subnet replacement or shared-network write permissions |
| Optional owned egress | `Microsoft.Network/natGateways/read`, `write`, `delete`, `join/action` and `Microsoft.Network/publicIPAddresses/read`, `write`, `delete`, `join/action` within the owned RG; exact-name/tag checks remain mandatory |
| Optional NAT attachment/removal | `Microsoft.Network/virtualNetworks/subnets/write` only on the explicitly selected dedicated GPU subnet, plus read/join as needed; preserve unrelated properties and enforce ETag checks |
| Publication | Release-container data writes, approved registry push/import and workspace environment/model version writes; no GPU Start or role-assignment authority |
| Deployment | Only the approved foundation/host/RBAC scopes; plan/apply runs behind protected environment approval, never untrusted PR code |
| Independent recovery | Native compute/job/owned-egress release and control-fence access on the same exact scopes; no Python, Docker or Terraform prerequisite |

Verified job operations include
`Microsoft.MachineLearningServices/workspaces/jobs/read`,
`Microsoft.MachineLearningServices/workspaces/jobs/write` and
`Microsoft.MachineLearningServices/workspaces/jobs/cancel/action`.

Azure's VNet guidance documents `Microsoft.Network/*/read` and
`Microsoft.Network/virtualNetworks/join/action` on the VNet, and
`Microsoft.Network/virtualNetworks/subnets/join/action` on the subnet. Do not
assume that giving only a subnet read permission satisfies compute provisioning.
Runtime grants must contain only the documented required operations; no blanket
Contributor/Network Contributor role or silent fallback.

Custom roles authorize resource types/actions, not the body of a subnet update.
Therefore subnet-write consent is a real boundary, not a promise of field-level RBAC.
It is unnecessary for customer-routed egress. The application must separately refuse
an unexpected CIDR, NSG, NAT, ID or ownership tag even when RBAC permits the request. The ownership tags
(`Studio:Regions:<r>:OwnershipTags`) are the proof of ownership: the studio creates, changes or deletes a compute,
NAT gateway or public IP, and deletes anything at Stop, only when the resource carries exactly those tags, so it can
never touch a customer resource with a similar name or ID.

Reference sources:

- [Azure ML workspace roles and custom roles](https://learn.microsoft.com/en-us/azure/machine-learning/how-to-assign-roles?view=azureml-api-2)
- [Secure training VNet prerequisites](https://learn.microsoft.com/en-us/azure/machine-learning/how-to-secure-training-vnet?view=azureml-api-2#prerequisites)
- [Azure networking permissions](https://learn.microsoft.com/en-us/azure/role-based-access-control/permissions/networking)
- [Managed Identity Operator](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/identity#managed-identity-operator)
- [Provider-operation metadata CLI](https://learn.microsoft.com/en-us/cli/azure/provider/operation#az-provider-operation-show)

The generated networking reference omits some NAT CRUD entries; the provider
operation metadata explicitly returned all four NAT and public-IP operations above.
The published Managed Identity Operator role uses a wildcard assignment pattern;
the provider metadata confirmed the exact identity `assign/action` operation.

## Verified-OFF Terraform handoff

Do not run these steps as part of ordinary CI. The migration requires an approved
maintenance window, a state backup outside Git and an exact customer selection.

1. Use the old supported Stop/recovery path while it still owns its resources.
2. Prove the old compute absent using `underlyingResourceAction=Delete`; prove all
   relevant jobs terminal and only the owned NAT detached/deleted, with PIP absent.
3. Persist the explicit tenant/subscription/deployment, landing-zone selection and
   preserved legacy v1 receipt (the one-time migration still starts from it) outside Git. Do not dump state or credentials into logs/artifacts.
4. Inspect state and verify each managed address belongs to the expected ID. The
   ephemeral state addresses are:

```text
azapi_resource.compute[0]
azurerm_subnet_nat_gateway_association.compute[0]
azurerm_nat_gateway_public_ip_association.compute[0]
azurerm_nat_gateway.compute[0]
azurerm_public_ip.compute[0]
```

5. Reconcile only verified-absent ephemeral entries. No broad state removal,
   live-resource forgetting, forced destroy or removal by resource-type wildcard.
   A duplicate/unexpected address or live resource blocks automatic handoff.
6. Map durable regional resources into `module.region["<selected-region>"]`,
   preserving every existing ID/name and deterministic role-assignment ID. Keep
   the shared host's root addresses stable unless an explicit reviewed move is needed.
7. Terraform moved addresses cannot interpolate a variable region. Use reviewed
   static moves or an exact customer-specific `state mv` mapping, not guessed syntax.
8. Apply only an approved saved plan proving no unintended replacement/deletion,
   landing-zone adoption or new compute. Terraform exports no machine contract: read the informational outputs and set the `Studio` settings (including the policy ceilings) by hand ([app-service-settings.md](app-service-settings.md)).
9. Deploy the new host disarmed. After validation, hosted Start can create freshly
   studio-owned ephemeral resources; it must not silently adopt a surviving old cluster.

The old owned NSG/association is durable and is not one of the ephemeral entries.
A customer-managed NSG and its association must remain outside managed lifecycle.

On failure, remain disarmed and retain the old recovery evidence. Reverse durable
state moves only while verified OFF, using the reviewed reverse mapping. Never
roll back by recreating compute automatically. A code/image rollback also requires
compatible `Studio` settings and a compatible release binding; compatibility cannot be inferred from a tag.

## Legacy jobs and outputs

During server cutover, legacy jobs need an explicit original-region binding. Match
their configured old workspace/compute/storage scope against the preserved legacy v1 receipt;
do not assign them whichever region is currently first or selected.

Retain original remote IDs, output prefixes and archive metadata. An unresolved
acceptance/cancellation must remain visible and fenced, not be converted into a new
job. Adding regions or changing limits must not rewrite the region of old results.

This guide defines the handoff and validation requirements. P3 implements and tests
the address/state mapping; P5/P6 implement durable fencing, hosted operations and
legacy job adapters before the old scripts are removed.

P3 intentionally narrows the legacy GPU account-wide Blob Data Contributor grant
to `wan-studio`, changing that assignment's scope/UUID so jobs cannot write the new
control container. Unchanged scopes retain their existing UUIDs. The shared host's
root addresses and legacy submitter role ID stay unchanged. Region tags and explicit
operator/publisher/control grants are additions; no foundation service replacement
is approved by this change.
