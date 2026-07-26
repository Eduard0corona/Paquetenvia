import { DriverStopsExperience } from "@/driver/components/driver-stops-experience";

export default async function DriverStopDetailPage({
  params,
}: Readonly<{ params: Promise<{ id: string }> }>) {
  const { id } = await params;
  return <DriverStopsExperience detailOrderId={id} />;
}
