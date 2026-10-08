import apiClient from './client';
import type {
  HealthResponse,
  ConnectionTestResponse,
  DatabaseListResponse,
} from '../types/database';

export const getHealth = () =>
  apiClient.get<HealthResponse>('/health').then((r) => r.data);

export const testConnection = () =>
  apiClient.get<ConnectionTestResponse>('/database/connection-test').then((r) => r.data);

export const listDatabases = () =>
  apiClient.get<DatabaseListResponse>('/api/databases').then((r) => r.data);
