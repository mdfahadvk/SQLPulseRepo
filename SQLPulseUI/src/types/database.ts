// ─── Database & Connection ───

export interface HealthResponse {
  status: string;
  application: string;
  version: string;
}

export interface ConnectionTestResponse {
  connected: boolean;
  message: string;
}

export interface DatabaseEntry {
  name: string;
  status: string;
}

export interface DatabaseListResponse {
  server: string;
  databases: DatabaseEntry[];
}
