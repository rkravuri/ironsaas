import { useTranslation } from "react-i18next";
import { SegmentedControl } from "@mantine/core";
import { densities, setDensity, useDensity, type Density } from "@/design/density";

const isDensity = (value: string): value is Density => (densities as readonly string[]).includes(value);

/** The grid's and the record pages' density, comfortable or compact (brief section 8), kept per viewer. */
export function DensitySwitch() {
  const { t } = useTranslation();
  const density = useDensity();
  return (
    <SegmentedControl
      size="xs"
      aria-label={t("density.label")}
      value={density}
      onChange={(value) => {
        if (isDensity(value)) {
          setDensity(value);
        }
      }}
      data={densities.map((value) => ({ value, label: t(`density.${value}`) }))}
    />
  );
}
