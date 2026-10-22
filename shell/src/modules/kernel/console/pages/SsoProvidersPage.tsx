import { useCallback } from "react";
import { useTranslation } from "react-i18next";
import { Link, useNavigate } from "react-router";
import { Button } from "@mantine/core";
import { IconPlus } from "@tabler/icons-react";
import { fetchSsoProviders, type SsoProvider } from "@/api/identity";
import { DataGrid, type DataGridColumn } from "@/components/data-grid";
import { ListPage } from "@/components/templates";
import { Mandala } from "@/design/patterns";

const defaultSort = [{ field: "name", direction: "asc" as const }];

/**
 * The tenant's company logins (TF-014, ADR-0048) on the list template: each provider with the domains it signs people
 * in for and what it does with them; a row opens the provider. Only holders of the critical identity.sso.manage see the
 * page, as the route says.
 */
export function SsoProvidersPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const fetchPage = useCallback((query: string, signal?: AbortSignal) => fetchSsoProviders(query, signal), []);

  const columns: DataGridColumn<SsoProvider>[] = [
    { field: "name", headerKey: "sso.list.name", sortable: true, filter: "text", flex: 2 },
    {
      field: "allowed_domains",
      headerKey: "sso.list.domains",
      filter: false,
      flex: 2,
      format: (provider) => provider.allowed_domains.join(", "),
    },
    {
      field: "just_in_time",
      headerKey: "sso.list.newPeople",
      filter: false,
      width: 190,
      format: (provider) => t(provider.just_in_time ? "sso.list.created" : "sso.list.notCreated"),
    },
    {
      field: "sso_only",
      headerKey: "sso.list.passwords",
      filter: false,
      width: 210,
      format: (provider) => t(provider.sso_only ? "sso.list.companyOnly" : "sso.list.passwordsToo"),
    },
    {
      field: "trust_provider_mfa",
      headerKey: "sso.list.mfa",
      filter: false,
      width: 220,
      format: (provider) => t(provider.trust_provider_mfa ? "sso.list.mfaTrusted" : "sso.list.mfaOwn"),
    },
  ];

  return (
    <ListPage
      title={t("sso.title")}
      description={t("sso.description")}
      drawing={
        <Mandala symmetry={8} rings={2} stroke={2} density={0.5} seed="single-sign-on" size={56} accent="console" />
      }
      actions={
        <Button component={Link} to="/console/sso/new" leftSection={<IconPlus size={16} aria-hidden="true" />}>
          {t("sso.list.new")}
        </Button>
      }
    >
      <DataGrid<SsoProvider>
        columns={columns}
        fetchPage={fetchPage}
        defaultSort={defaultSort}
        label={t("sso.title")}
        onRowOpen={(provider) => void navigate(`/console/sso/${provider.id}`)}
      />
    </ListPage>
  );
}
