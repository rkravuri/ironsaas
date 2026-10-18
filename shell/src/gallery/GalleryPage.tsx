import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { useSearchParams } from "react-router";
import { Container, Stack, Text, Title, useMantineColorScheme } from "@mantine/core";
import { readDensity, setDensity } from "@/design/density";
import { ColourSection } from "./ColourSection";
import { ComponentsSection } from "./ComponentsSection";
import { DrawingsSection } from "./DrawingsSection";
import { GalleryControls } from "./GalleryControls";
import { galleryStateFrom } from "./gallery-state";
import { GridSection } from "./GridSection";
import { SpaceAndMotionSection } from "./SpaceAndMotionSection";
import { TypeSection } from "./TypeSection";
import { ViewsSection } from "./ViewsSection";

/**
 * The gallery (ADR-0024 item 7, ADR-0025): every token and every themed component, in the scheme and density the
 * URL names. Development builds only; the accessibility scan reads it and waits for the ready mark the grid sets.
 */
export default function GalleryPage() {
  const { t } = useTranslation();
  const [params] = useSearchParams();
  const { scheme, density } = galleryStateFrom(params);
  const { setColorScheme, clearColorScheme } = useMantineColorScheme();
  const [gridReady, setGridReady] = useState(false);
  const densityBefore = useRef(readDensity());

  useEffect(() => {
    setColorScheme(scheme);
    setDensity(density);
  }, [scheme, density, setColorScheme]);

  // Leaving the gallery restores what the viewer had: the gallery is a tool, not a preference.
  useEffect(() => {
    const restore = densityBefore.current;
    return () => {
      clearColorScheme();
      setDensity(restore);
    };
  }, [clearColorScheme]);

  return (
    <Container size="lg" py="xl" data-gallery-ready={gridReady ? "true" : undefined}>
      <Stack gap="xl">
        <header>
          <Title order={1} variant="display">
            {t("gallery.title")}
          </Title>
          <Text c="dimmed" mt="xs">
            {t("gallery.intro")}
          </Text>
        </header>
        <GalleryControls scheme={scheme} density={density} />
        <ColourSection />
        <TypeSection />
        <SpaceAndMotionSection />
        <ComponentsSection />
        <DrawingsSection />
        <GridSection onReady={() => setGridReady(true)} />
        <ViewsSection />
      </Stack>
    </Container>
  );
}
