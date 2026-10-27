"""Framework-free migration/ownership tests; native reads are injected, never sent to Azure."""
import copy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import urllib.error

import migration

HERE = Path(__file__).resolve().parent


def fixture():
    config = json.loads((HERE / "tests" / "fixtures" / "regional-inputs.json").read_text())
    receipt = json.loads((HERE / "tests" / "fixtures" / "studio-off.json").read_text(encoding="utf-8-sig"))
    region, chosen, ids = migration.context(config, receipt)
    state = {"version": 4, "terraform_version": "1.16.6", "outputs": {}, "serial": 7,
             "lineage": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "resources": []}
    def add(address, attributes):
        index = None
        if "[" in address:
            address, raw = address.split("[", 1)
            index = json.loads(raw[:-1])
        kind, name = address.split(".")
        instance = {"schema_version": 0, "attributes": attributes}
        if index is not None:
            instance["index_key"] = index
        provider = "terraform.io/builtin/terraform" if kind == "terraform_data" else (
            "registry.terraform.io/azure/azapi" if kind == "azapi_resource" else "registry.terraform.io/hashicorp/azurerm")
        existing = next((resource for resource in state["resources"] if resource["type"] == kind and resource["name"] == name), None)
        if existing:
            existing["instances"].append(instance)
        else:
            state["resources"].append({"mode": "managed", "type": kind, "name": name,
                                       "provider": f'provider["{provider}"]', "instances": [instance]})
    add("azurerm_user_assigned_identity.workspace", {"id": ids["workspace_identity"], "principal_id": "44444444-4444-4444-4444-444444444444"})
    add("azurerm_user_assigned_identity.compute", {"id": ids["compute_identity"], "principal_id": "66666666-6666-6666-6666-666666666666",
                                                "client_id": receipt["computeIdentityClientId"]})
    expected, grants = migration.expected_resources(config, receipt, migration.state_resources(state), region, chosen, ids)
    for address, identity in expected.items():
        if address.startswith("azurerm_user_assigned_identity"):
            continue
        attributes = {"id": identity}
        if address.startswith("azurerm_private_endpoint."):
            key = "workspace" if address.endswith(".workspace") else address.split('["')[1].split('"]')[0]
            zones = [chosen["private_dns_zone_ids"][name] for name in (("api", "notebooks") if key == "workspace" else (key,))]
            attributes.update(subnet_id=chosen["private_endpoint_subnet_id"], private_dns_zone_group=[{"private_dns_zone_ids": zones}])
        if address == "azurerm_application_insights.studio":
            attributes["workspace_id"] = chosen["log_analytics_workspace_id"]
        if address == "azurerm_subnet_network_security_group_association.compute[0]":
            attributes.update(subnet_id=chosen["gpu_subnet_id"], network_security_group_id=ids["nsg"])
        if address in grants:
            attributes.update(zip(("scope", "principal_id", "role_definition_id"), grants[address]))
        add(address, attributes)
    add("terraform_data.landing_zone", {"id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "input": {
        "gpu_subnet_id": chosen["gpu_subnet_id"], "private_endpoint_subnet_id": chosen["private_endpoint_subnet_id"],
        "gpu_subnet_cidr": chosen["gpu_subnet_cidr"],
    }})
    return config, receipt, state, ids, chosen


def reader(receipt, ids, chosen):
    owned = lambda identity: {"id": identity, "tags": receipt["ownershipTags"]}
    responses = {
        ids["group"]: owned(ids["group"]), ids["workspace"]: owned(ids["workspace"]),
        ids["compute"]: None, ids["nat"]: None, ids["pip"]: None,
        chosen["gpu_subnet_id"]: {"id": chosen["gpu_subnet_id"], "properties": {
            "addressPrefix": chosen["gpu_subnet_cidr"], "defaultOutboundAccess": False,
            "networkSecurityGroup": {"id": ids["nsg"]},
        }},
        ids["workspace"] + "/jobs?api-version=2024-10-01": {"value": []},
    }
    calls = []
    def read(path, api):
        calls.append((path, api))
        if path not in responses:
            raise AssertionError("Unexpected ARM read")
        return responses[path]
    return read, responses, calls


class MigrationTests(unittest.TestCase):
    def test_default_assessment_is_read_only_and_cannot_authorize_state_commands(self):
        config, receipt, state, _, _ = fixture()
        before = copy.deepcopy(state)
        result = migration.assess(config, receipt, state)
        self.assertTrue(result["readOnly"])
        self.assertFalse(result["verifiedOff"])
        self.assertEqual([], result["commands"])
        self.assertGreater(len(result["moves"]), 15)
        self.assertEqual(before, state)

    def test_committed_mapping_fixture_matches_the_read_only_assessor(self):
        config, receipt, generated, _, _ = fixture()
        stored = json.loads((HERE / "tests" / "fixtures" / "migration-state.json").read_text())
        self.assertEqual(generated, stored)
        report = migration.assess(config, receipt, stored)
        self.assertFalse(report["verifiedOff"])
        self.assertEqual([], report["commands"])

    def test_verified_off_emits_exact_moves_and_inverse_mapping(self):
        config, receipt, state, ids, chosen = fixture()
        read, _, calls = reader(receipt, ids, chosen)
        result = migration.assess(config, receipt, state, read)
        self.assertTrue(result["verifiedOff"])
        self.assertTrue(all(command.startswith("terraform -chdir=infra state mv -lock-timeout=60s ") for command in result["commands"]))
        self.assertTrue(all(value["to"].startswith('module.region["eastus2"].') for value in result["moves"]))
        self.assertEqual(result["moves"][0]["from"], result["rollbackMoves"][-1]["to"])
        self.assertEqual(7, result["stateSerial"])
        self.assertIn((ids["compute"], "2024-10-01"), calls)

    def test_scope_names_and_subnets_are_bound_to_explicit_new_configuration(self):
        for field, value in (("tenantId", "99999999-9999-9999-9999-999999999999"),
                             ("workspaceId", "/subscriptions/x/workspaces/unrelated"),
                             ("natGatewayId", "/subscriptions/x/natGateways/unrelated"),
                             ("gpuSubnetId", "/subscriptions/x/subnets/unrelated")):
            with self.subTest(field=field):
                config, receipt, state, _, _ = fixture()
                receipt[field] = value
                with self.assertRaises(ValueError):
                    migration.assess(config, receipt, state)

    def test_forged_role_scope_or_id_is_rejected_not_moved(self):
        for field in ("id", "scope", "principal_id", "role_definition_id"):
            with self.subTest(field=field):
                config, receipt, state, _, _ = fixture()
                grant = next(resource for resource in state["resources"] if resource["type"] == "azurerm_role_assignment")
                grant["instances"][0]["attributes"][field] = "unexpected"
                with self.assertRaises(ValueError):
                    migration.assess(config, receipt, state)

    def test_changed_compute_grant_is_reported_as_intentional_access_reduction(self):
        config, receipt, state, ids, _ = fixture()
        result = migration.assess(config, receipt, state)
        reduction = result["intentionalRoleChange"]
        self.assertEqual(ids["storage"], reduction["fromScope"])
        self.assertEqual(ids["storage"] + "/blobServices/default/containers/wan-studio", reduction["toScope"])
        self.assertNotIn("state rm", str(result["moves"]))

    def test_dns_logging_and_endpoint_subnet_changes_are_not_hidden_by_state_moves(self):
        for kind, field, bad in (("azurerm_private_endpoint", "subnet_id", "unrelated"),
                                 ("azurerm_private_endpoint", "private_dns_zone_group", []),
                                 ("azurerm_application_insights", "workspace_id", "unrelated")):
            with self.subTest(field=field):
                config, receipt, state, _, _ = fixture()
                resource = next(value for value in state["resources"] if value["type"] == kind)
                resource["instances"][0]["attributes"][field] = bad
                with self.assertRaises(ValueError):
                    migration.assess(config, receipt, state)

    def test_unknown_or_deposed_managed_state_is_never_forgotten(self):
        for changed in ("unknown", "deposed"):
            with self.subTest(changed=changed):
                config, receipt, state, _, _ = fixture()
                if changed == "unknown":
                    state["resources"][0]["name"] = "somebody_else"
                else:
                    state["resources"][0]["instances"][0]["deposed"] = "unresolved"
                with self.assertRaises(ValueError):
                    migration.assess(config, receipt, state)

    def test_already_moved_resources_are_noops_and_duplicate_targets_are_rejected(self):
        config, receipt, state, _, _ = fixture()
        moved = copy.deepcopy(state)
        for resource in moved["resources"]:
            resource["module"] = 'module.region["eastus2"]'
        result = migration.assess(config, receipt, moved)
        self.assertEqual([], result["moves"])
        state["resources"] += moved["resources"]
        with self.assertRaises(ValueError):
            migration.assess(config, receipt, state)

    def test_ephemeral_entries_can_be_removed_only_after_independent_absence_reads(self):
        config, receipt, state, ids, chosen = fixture()
        for kind, identity in (("azapi_resource", ids["compute"]), ("azurerm_nat_gateway", ids["nat"]),
                               ("azurerm_public_ip", ids["pip"]), ("azurerm_subnet_nat_gateway_association", chosen["gpu_subnet_id"]),
                               ("azurerm_nat_gateway_public_ip_association", ids["nat"] + "|" + ids["pip"])):
            provider = "registry.terraform.io/azure/azapi" if kind == "azapi_resource" else "registry.terraform.io/hashicorp/azurerm"
            state["resources"].append({"mode": "managed", "type": kind, "name": "compute",
                                       "provider": f'provider["{provider}"]', "instances": [{"index_key": 0, "attributes": {"id": identity}}]})
        self.assertEqual([], migration.assess(config, receipt, state)["commands"])
        read, _, _ = reader(receipt, ids, chosen)
        result = migration.assess(config, receipt, state, read)
        removals = [command for command in result["commands"] if " state rm " in command]
        self.assertEqual(5, len(removals))
        self.assertFalse(any("*" in command for command in removals))

    def test_live_or_unknown_resources_block_all_state_commands(self):
        for key in ("compute", "nat", "pip"):
            with self.subTest(key=key):
                config, receipt, state, ids, chosen = fixture()
                read, responses, _ = reader(receipt, ids, chosen)
                responses[ids[key]] = {"id": ids[key]}
                with self.assertRaises(ValueError):
                    migration.assess(config, receipt, state, read)

    def test_subnet_foreign_nat_nsg_cidr_and_outbound_changes_block_handoff(self):
        changes = [{"natGateway": {"id": "foreign"}}, {"networkSecurityGroup": {"id": "foreign"}},
                   {"addressPrefix": "10.42.9.0/26"}, {"defaultOutboundAccess": True}]
        for change in changes:
            with self.subTest(change=change):
                config, receipt, state, ids, chosen = fixture()
                read, responses, _ = reader(receipt, ids, chosen)
                responses[chosen["gpu_subnet_id"]]["properties"].update(change)
                with self.assertRaises(ValueError):
                    migration.assess(config, receipt, state, read)

    def test_pagination_is_fully_read_and_nonterminal_or_unknown_jobs_fail(self):
        for status in ("Queued", "Running", None):
            with self.subTest(status=status):
                config, receipt, state, ids, chosen = fixture()
                read, responses, _ = reader(receipt, ids, chosen)
                next_page = "https://management.azure.com" + ids["workspace"] + "/jobs?api-version=2024-10-01&skip=2"
                responses[ids["workspace"] + "/jobs?api-version=2024-10-01"] = {"value": [], "nextLink": next_page}
                responses[next_page] = {"value": [{"properties": {"computeId": ids["compute"], "status": status}}]}
                with self.assertRaises(ValueError):
                    migration.assess(config, receipt, state, read)

    def test_external_continuation_or_wrong_resource_tags_fail_closed(self):
        config, receipt, state, ids, chosen = fixture()
        read, responses, _ = reader(receipt, ids, chosen)
        responses[ids["workspace"] + "/jobs?api-version=2024-10-01"]["nextLink"] = "https://unexpected.example/jobs"
        with self.assertRaises(ValueError):
            migration.assess(config, receipt, state, read)
        responses[ids["workspace"] + "/jobs?api-version=2024-10-01"] = {"value": []}
        responses[ids["workspace"]]["tags"] = {}
        with self.assertRaises(ValueError):
            migration.assess(config, receipt, state, read)

    def test_shared_host_must_be_stopped_not_merely_configured_disarmed(self):
        config, receipt, _, ids, chosen = fixture()
        config["portal"] = {"subnet_id": chosen["gpu_subnet_id"].replace("wan-gpu", "portal"), "sku": "B1"}
        receipt["portal"] = {"id": ids["group"] + "/providers/Microsoft.Web/sites/app-wan-demo-14173233",
                             "identityPrincipalId": "88888888-8888-8888-8888-888888888888",
                             "identityClientId": "99999999-9999-9999-9999-999999999999"}
        _, _, ids = migration.context(config, receipt)
        read, responses, _ = reader(receipt, ids, chosen)
        site = {"id": ids["portal"], "tags": receipt["ownershipTags"], "properties": {"state": "Running"}}
        responses[ids["portal"]] = site
        with self.assertRaises(ValueError):
            migration.verify_off(read, receipt, chosen, ids)
        site["properties"]["state"] = "Stopped"
        migration.verify_off(read, receipt, chosen, ids)

    def test_shared_host_addresses_and_composite_role_id_are_retained(self):
        config, receipt, state, ids, chosen = fixture()
        stem = "wan-demo-14173233"
        subnet = chosen["gpu_subnet_id"].replace("wan-gpu", "portal")
        zone = chosen["private_dns_zone_ids"]["blob"].replace("privatelink.blob.core.windows.net", "privatelink.azurewebsites.net")
        config["portal"] = {"subnet_id": subnet, "dns_zone_id": zone, "sku": "B1"}
        receipt["portal"] = {"id": ids["group"] + f"/providers/Microsoft.Web/sites/app-{stem}",
                             "identityPrincipalId": "88888888-8888-8888-8888-888888888888",
                             "identityClientId": "99999999-9999-9999-9999-999999999999"}
        role = str(migration.uuid.uuid5(migration.uuid.NAMESPACE_URL, (ids["workspace"] + "/wan-portal-job-submitter").lower()))
        resources = [
            ("azurerm_user_assigned_identity", "portal", {"id": ids["group"] + f"/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-{stem}-portal",
                "principal_id": receipt["portal"]["identityPrincipalId"], "client_id": receipt["portal"]["identityClientId"]}),
            ("azurerm_service_plan", "portal", {"id": ids["group"] + f"/providers/Microsoft.Web/serverFarms/asp-{stem}", "sku_name": "B1"}),
            ("azurerm_linux_web_app", "portal", {"id": receipt["portal"]["id"], "virtual_network_subnet_id": subnet}),
            ("azurerm_private_endpoint", "portal", {"id": ids["group"] + f"/providers/Microsoft.Network/privateEndpoints/pe-{stem}-portal",
                "subnet_id": chosen["private_endpoint_subnet_id"], "private_dns_zone_group": [{"private_dns_zone_ids": [zone]}]}),
            ("azurerm_role_definition", "portal_job_submitter", {"id": f"/subscriptions/{config['subscription_id']}/providers/Microsoft.Authorization/roleDefinitions/{role}|{ids['workspace']}"}),
        ]
        for kind, name, attributes in resources:
            state["resources"].append({"mode": "managed", "type": kind, "name": name,
                "provider": 'provider["registry.terraform.io/hashicorp/azurerm"]',
                "instances": [{"index_key": 0, "attributes": attributes}]})
        result = migration.assess(config, receipt, state)
        self.assertEqual(5, len(result["sharedHostAddressesRetained"]))
        self.assertFalse(any(".portal" in value["from"] for value in result["moves"]))
        state["resources"][-1]["instances"][0]["attributes"]["id"] = state["resources"][-1]["instances"][0]["attributes"]["id"].split("|")[0]
        with self.assertRaises(ValueError):
            migration.assess(config, receipt, state)

    def test_show_json_and_duplicate_input_keys_are_not_a_state_backup(self):
        config, receipt, _, _, _ = fixture()
        with self.assertRaises(ValueError):
            migration.assess(config, receipt, {"format_version": "1.0", "values": {}})
        with tempfile.TemporaryDirectory() as scratch:
            path = Path(scratch) / "input.json"
            path.write_text('{"subscription_id":"one","subscription_id":"two"}')
            with self.assertRaises(ValueError):
                migration.load(path)

    def test_cli_scope_mismatch_refuses_token_or_resource_reads(self):
        config, _, _, _, _ = fixture()
        account = json.dumps({"id": "99999999-9999-9999-9999-999999999999", "tenantId": config["tenant_id"]})
        with patch.object(migration.shutil, "which", return_value="az"), patch.object(migration.subprocess, "check_output", return_value=account) as calls:
            with self.assertRaises(ValueError):
                migration.live_reader(config)
        self.assertEqual(1, calls.call_count)

    def test_live_adapter_sends_only_gets_and_404_is_not_reused_for_other_errors(self):
        config, _, _, ids, _ = fixture()
        account = json.dumps({"id": config["subscription_id"], "tenantId": config["tenant_id"]})
        with patch.object(migration.shutil, "which", return_value="az"), patch.object(migration.subprocess, "check_output", side_effect=[account, "fixture-token"]):
            read = migration.live_reader(config)
        for status, exception in ((404, None), (403, RuntimeError), (500, RuntimeError)):
            error = urllib.error.HTTPError("https://management.azure.com/", status, "fixture", {}, None)
            with patch.object(migration.urllib.request, "urlopen", side_effect=error) as call:
                if exception:
                    with self.assertRaises(exception):
                        read(ids["compute"], "2024-10-01")
                else:
                    self.assertIsNone(read(ids["compute"], "2024-10-01"))
                self.assertEqual("GET", call.call_args.args[0].get_method())

    def test_source_configuration_has_no_ephemeral_or_customer_network_owners(self):
        forbidden = ("azurerm_virtual_network", "azurerm_subnet", "azurerm_private_dns_zone", "azurerm_log_analytics_workspace",
                     "azurerm_public_ip", "azurerm_nat_gateway", "azurerm_subnet_nat_gateway_association")
        import re
        for path in [*HERE.glob("*.tf"), * (HERE / "modules" / "region").glob("*.tf")]:
            source = path.read_text()
            for kind in forbidden:
                self.assertNotRegex(source, rf'resource\s+"{kind}"\s+"')
            self.assertNotRegex(source, r'resource\s+"azapi_resource"\s+"compute"')
            self.assertNotRegex(source, r'ignore_changes\s*=\s*all')
            self.assertNotRegex(source, r'removed\s*\{')
        example = json.loads((HERE / "terraform.tfvars.json.example").read_text())
        self.assertTrue(all(not value.get("compute_enabled", False) and value["max_nodes"] == 1 and
                            value["max_job_minutes"] == 120 for value in example["regions"].values()))

    def test_saved_plan_refuses_unmoved_root_resources_and_unexpected_role_deletes(self):
        config, receipt, state, ids, _ = fixture()
        report = migration.assess(config, receipt, state)
        for address, kind in (("azurerm_resource_group.studio", "azurerm_resource_group"),
                              ('module.region["eastus2"].azurerm_role_assignment.service["workspace_registry"]', "azurerm_role_assignment")):
            with self.subTest(address=address):
                plan = {"format_version": "1.2", "complete": True, "resource_changes": [{
                    "mode": "managed", "address": address, "type": kind,
                    "change": {"actions": ["delete", "create"], "before": {"id": ids["group"]}, "after": {}},
                }]}
                with self.assertRaises(ValueError):
                    migration.validate_plan(plan, report)

    def test_saved_plan_accepts_only_the_exact_gpu_blob_role_replacement(self):
        config, receipt, state, _, _ = fixture()
        report = migration.assess(config, receipt, state)
        change = report["intentionalRoleChange"]
        role = f"/subscriptions/{config['subscription_id']}/providers/Microsoft.Authorization/roleDefinitions/ba92f5b4-2d11-453d-a403-e96b0029c9fe"
        before = {"id": change["oldId"], "scope": change["fromScope"], "principal_id": change["principalId"], "role_definition_id": role}
        after = before | {"scope": change["toScope"], "name": str(migration.uuid.uuid5(migration.uuid.NAMESPACE_URL,
            f"{change['toScope']}/{change['principalId']}/{role.rsplit('/',1)[1]}".lower()))}
        plan = {"format_version": "1.2", "resource_changes": [{"mode": "managed", "address": change["address"],
            "type": "azurerm_role_assignment", "change": {"actions": ["delete", "create"], "before": before, "after": after}}]}
        self.assertTrue(migration.validate_plan(plan, report))
        after["scope"] = change["fromScope"]
        with self.assertRaises(ValueError):
            migration.validate_plan(plan, report)

    def test_saved_plan_cannot_create_new_compute_or_customer_networking(self):
        config, receipt, state, _, _ = fixture()
        report = migration.assess(config, receipt, state)
        for kind, after in (("azapi_resource", {"type": "Microsoft.MachineLearningServices/workspaces/computes@2024-10-01"}),
                            ("azurerm_subnet", {}), ("azurerm_nat_gateway", {})):
            plan = {"format_version": "1.2", "resource_changes": [{"mode": "managed", "address": "forbidden", "type": kind,
                "change": {"actions": ["create"], "after": after}}]}
            with self.assertRaises(ValueError):
                migration.validate_plan(plan, report)


if __name__ == "__main__":
    unittest.main(verbosity=2)
