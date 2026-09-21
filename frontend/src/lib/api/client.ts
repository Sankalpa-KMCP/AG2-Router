import type {
  AccountsListResponse,
  AccountMetadata,
  SystemStatusDto,
  SwitchPlanDto,
  SwitchStatusDto,
  RouterConfigDto
} from './types.js';

class ApiClient {
  private switchIntentToken: string | null = null;

  private async request<T>(url: string, init?: RequestInit): Promise<T> {
    const headers: Record<string, string> = {
      ...(init?.headers as Record<string, string> || {})
    };

    if (init?.body && typeof init.body === 'string' && !headers['Content-Type']) {
      headers['Content-Type'] = 'application/json';
    }

    const res = await fetch(url, {
      ...init,
      headers
    });

    // Capture switch token if present
    const switchHeader = res.headers.get('x-ag2-switch-token');
    if (switchHeader) {
      this.switchIntentToken = switchHeader;
    }

    if (!res.ok) {
      let errorMsg = `HTTP ${res.status}`;
      try {
        const errorJson = await res.json();
        if (errorJson && errorJson.error) {
          errorMsg = errorJson.error;
        }
      } catch {
        // Fall back to status text
        errorMsg = res.statusText || errorMsg;
      }
      throw new Error(errorMsg);
    }

    return await res.json() as T;
  }

  public async getStatus(): Promise<SystemStatusDto> {
    return this.request<SystemStatusDto>('/api/status');
  }

  public async getAccounts(): Promise<AccountsListResponse> {
    return this.request<AccountsListResponse>('/api/accounts');
  }

  public async createAccount(data: {
    email: string;
    name?: string;
    priority?: number;
    isReserve?: boolean;
    alias?: string;
  }): Promise<{ account: AccountMetadata }> {
    return this.request<{ account: AccountMetadata }>('/api/accounts', {
      method: 'POST',
      body: JSON.stringify(data)
    });
  }

  public async updateAccountAlias(id: string, alias: string): Promise<{ success: boolean; account: AccountMetadata }> {
    return this.request<{ success: boolean; account: AccountMetadata }>(`/api/accounts/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      body: JSON.stringify({ alias })
    });
  }

  public async enrollCurrentAccount(): Promise<{ success: boolean; account: AccountMetadata; message?: string }> {
    return this.request<{ success: boolean; account: AccountMetadata; message?: string }>('/api/accounts/enroll-current', {
      method: 'POST',
      body: JSON.stringify({})
    });
  }

  public async deleteAccount(id: string): Promise<{ success: boolean; removedId: string; vaultRecordDeleted?: boolean }> {
    return this.request<{ success: boolean; removedId: string; vaultRecordDeleted?: boolean }>(`/api/accounts/${encodeURIComponent(id)}`, {
      method: 'DELETE'
    });
  }

  public async getSwitchStatus(): Promise<{ status: SwitchStatusDto }> {
    return this.request<{ status: SwitchStatusDto }>('/api/switching/status');
  }

  public async planSwitch(id: string): Promise<{ plan: SwitchPlanDto }> {
    return this.request<{ plan: SwitchPlanDto }>(`/api/accounts/${encodeURIComponent(id)}/switch-plan`, {
      method: 'POST'
    });
  }

  public async executeSwitch(id: string): Promise<{ success: boolean; code?: number; message?: string }> {
    // If we don't have a token, attempt to acquire one first
    if (!this.switchIntentToken) {
      await this.getSwitchStatus();
    }

    const headers: Record<string, string> = {};
    if (this.switchIntentToken) {
      headers['X-AG2-Switch-Token'] = this.switchIntentToken;
    }

    return this.request<{ success: boolean; code?: number; message?: string }>(`/api/accounts/${encodeURIComponent(id)}/switch`, {
      method: 'POST',
      headers,
      body: JSON.stringify({ confirm: true })
    });
  }

  public async getConfig(): Promise<{ config: RouterConfigDto }> {
    return this.request<{ config: RouterConfigDto }>('/api/config');
  }

  public async saveConfig(config: {
    autoSwitchEnabled: boolean;
    lowQuotaThresholdPercent: number;
    minimumCandidateQuotaPercent: number;
    pollingIntervalMs: number;
  }): Promise<{ success: boolean; config: RouterConfigDto }> {
    return this.request<{ success: boolean; config: RouterConfigDto }>('/api/config', {
      method: 'POST',
      body: JSON.stringify(config)
    });
  }
}

export const api = new ApiClient();
